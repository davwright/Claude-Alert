using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHook;

internal static partial class Program
{
    // Pipe protocol — see DESKTOPNAMES-INTEGRATION.md.
    private const string PipeName = "DesktopNames";
    private const int PipeConnectTimeoutMs = 150;
    private const int BusyDebounceMs = 1000;

    private static readonly string CacheDir =
        Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "claude-alert");

    private static readonly string LogPath = Path.Combine(CacheDir, "claudehook.log");

    /// <summary>Commit this binary was built from, plus <c>.dirty</c> when the tree didn't
    /// match it. Stamped by publish.ps1; release\ is gitignored so this is the only link
    /// back to source. Logged on every invocation.</summary>
    private static readonly string BuildStamp =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unstamped";
    // Timing bugs get noticed hours later, so keep 4-8h: roll to .1 once the file is 4h old.
    private static readonly TimeSpan LogMaxAge = TimeSpan.FromHours(4);
    private static readonly Stopwatch RunClock = Stopwatch.StartNew();

    private static void Log(string msg)
    {
        try
        {
            var fi = new FileInfo(LogPath);
            bool rotated = false;
            if (fi.Exists && DateTime.UtcNow - fi.CreationTimeUtc >= LogMaxAge)
            {
                File.Move(LogPath, LogPath + ".1", overwrite: true);
                rotated = true;
            }
            File.AppendAllText(LogPath,
                $"{DateTime.UtcNow:HH:mm:ss.fff} pid={Environment.ProcessId,5} +{RunClock.ElapsedMilliseconds,5}ms  {msg}\n");
            // Windows tunneling hands a re-created name the old file's creation time for ~15s.
            if (rotated) File.SetCreationTimeUtc(LogPath, DateTime.UtcNow);
        }
        catch { }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            "ClaudeHook — bridges Claude Code hook events to DesktopNames (and Windows toasts as fallback).\n" +
            "\n" +
            "Typical use: double-click the exe. With no arguments and no stdin redirection, it\n" +
            "self-installs (wires the hooks, exec form, into ~/.claude/settings.json,\n" +
            "registers Windows autostart) and pops a toast with the result.\n" +
            "\n" +
            "Other entry points:\n" +
            "  ClaudeHook.exe --install             Same as double-click. Useful from scripts.\n" +
            "  ClaudeHook.exe --healthcheck         Verify install, show a toast, exit. Runs from\n" +
            "                                       the autostart Run key at every Windows login.\n" +
            "  ClaudeHook.exe [--from-pid <pid>]    Hook path. Stdin = hook JSON. Run by Claude\n" +
            "                                       Code as an exec-form hook (no shell); the\n" +
            "                                       walk starts at our own parent (claude.exe).\n" +
            "                                       Optional: --notification-kind <permission_prompt|\n" +
            "                                       idle_prompt|auth_success> to pass the Notification\n" +
            "                                       matcher subtype.\n" +
            "  ClaudeHook.exe --help                Show this message.\n" +
            "\n" +
            "Hook JSON expected on stdin: { \"hook_event_name\": \"Stop\", \"session_id\": \"...\", \"cwd\": \"...\" }.\n" +
            "Log: %TEMP%\\claude-alert\\claudehook.log (rolls to .1 every 4h).");
    }

    private static int Main(string[] args)
    {
        // Subcommands (explicit).
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--install")     return Install.RunWithToast();
            if (args[i] == "--healthcheck") return HealthCheck.Run();
            if (args[i] == "--help" || args[i] == "-h") { PrintHelp(); return 0; }
        }

        // Zero args + no stdin redirection = user double-clicked the icon. Treat as
        // install: a downloaded exe should "just work" when run interactively. The
        // toast confirms what happened. Same code path as --install.
        if (args.Length == 0 && !Console.IsInputRedirected)
            return Install.RunWithToast();

        try
        {
            Directory.CreateDirectory(CacheDir);
            Log($"START {BuildStamp}");

            // --from-pid <N> — bash hands us its parent's PID (claude.exe).
            // We walk from there to find Code.exe. The Claude process is alive for
            // the entire session so its PID is stable across hooks.
            //
            // (Older bash wrappers passed --vscode-pid directly; honor that too.)
            int fromPid   = 0;
            int vscodePid = 0;
            string? notificationKind = null;   // permission_prompt | idle_prompt | auth_success
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--from-pid"          && int.TryParse(args[i + 1], out int fp)) fromPid   = fp;
                if (args[i] == "--vscode-pid"        && int.TryParse(args[i + 1], out int vp)) vscodePid = vp;
                if (args[i] == "--notification-kind") notificationKind = args[i + 1];
            }

            var hook = ReadHookJsonFromStdin();
            if (hook == null) { Log("EXIT no-stdin"); return 0; }

            // Walk the parent chain. Returns the full chain + an outcome label so
            // DesktopNames knows whether to trust vscodePid or fall back to other
            // resolution strategies (cwd lookup, etc.). Done before the filters below
            // because the heartbeat reports every event, subagent and debounced ones too.
            var walk = WalkChain(hook.SessionId, fromPid);
            if (vscodePid == 0) vscodePid = walk.VsCodePid;
            try { WriteHeartbeat(hook, LivenessPid(fromPid, walk.Chain), vscodePid); }
            catch (Exception ex) { Log($"  heartbeat write failed: {ex.Message}"); }

            // InternalKind is ClaudeHook-side only: drives the busy debounce and the
            // balloon-fallback decision when the pipe is down. NOT sent to DN — DN keys
            // off `hookEvent` and decides colour itself.
            string internalKind = InternalKind(hook.HookEventName);
            Log($"hook event={hook.HookEventName} kind={notificationKind ?? "-"} session={TruncSession(hook.SessionId)} cwd={hook.Cwd} -> internalKind={internalKind}");
            if (internalKind == "") { Log("EXIT unknown-event"); return 0; }

            // auth_success is just "auth completed OK" — not user-actionable. Drop on the floor.
            // (We still subscribe so the matcher is visible in settings.json, but we filter here.)
            if (notificationKind == "auth_success")
            {
                Log("EXIT notification=auth_success (suppressed)");
                return 0;
            }

            // A subagent tool call (agent_id present) is the parent's own work seen from the
            // inside: the parent already went busy firing PreToolUse for the Agent tool, so
            // re-announcing every nested call only churns the desktop colour. Anything the
            // user actually has to answer (PermissionRequest / Notification -> "asking")
            // still gets through, whoever asked.
            if (!string.IsNullOrEmpty(hook.AgentId) && internalKind == "busy")
            {
                Log($"EXIT subagent busy event (agent={hook.AgentType}/{hook.AgentId})");
                return 0;
            }

            // PostToolUse is DN's only "the permission prompt was answered" signal, and with
            // parallel tools two can land inside the 1s window — losing one would strand the
            // desktop yellow for the rest of the turn. But that only matters while a prompt is
            // actually outstanding, which is rare (2 of 1394 events in a day's logs), and a
            // blanket exemption makes PostToolUse the largest event category by far. So keep
            // the exemption exactly when it earns its keep: an ask is pending, or this is the
            // Monitor PostToolUse that arms liveness further down.
            bool exemptFromDebounce = hook.HookEventName == "PostToolUse"
                                      && (hook.ToolName == "Monitor" || HasPendingAsk(hook.SessionId));
            if (internalKind == "busy" && !exemptFromDebounce && IsBusyDebounced(hook.SessionId))
            {
                Log("EXIT busy-debounced");
                return 0;
            }

            // Ask lifecycle, updated only for events that actually reached this point: an
            // asking event raises the flag; the PostToolUse that answers it, or the end of
            // the turn, lowers it again.
            if (internalKind == "asking") SetPendingAsk(hook.SessionId, true);
            else if (hook.HookEventName is "PostToolUse" or "Stop" or "StopFailure" or "SessionEnd")
                SetPendingAsk(hook.SessionId, false);

            // For Stop, peek the transcript to find the assistant's last text. Lets DN
            // colour "Stop ending with a question" differently from "clean Stop". On
            // schema break, pop a one-shot Windows toast per session (the heuristic
            // depends on Claude's transcript format which isn't a stable contract).
            // Monitor liveness is tracked in a sidecar rather than rediscovered from the
            // transcript on every Stop: a Monitor's arm line scrolls out of any fixed window
            // long before the watcher finishes. Arm here, expire at Stop, drop at SessionEnd.
            if (hook.HookEventName == "PostToolUse" && hook.ToolName == "Monitor")
            {
                TranscriptPeek.ArmMonitor(CacheDir, hook.SessionId, hook.ToolResponse?.ToString());
                Log("  monitor armed");
            }
            if (hook.HookEventName == "SessionEnd")
                TranscriptPeek.ClearMonitors(CacheDir, hook.SessionId);

            bool? endsWithQuestion = null;
            string? messageTail = null;
            bool backgroundActive = false;
            if (hook.HookEventName == "Stop")
            {
                var peek = TranscriptPeek.Read(hook.TranscriptPath);
                Log($"  transcriptPeek outcome={peek.Outcome}" +
                    (peek.ParseError != null ? $" err='{peek.ParseError}'" : ""));
                if (peek.Outcome == TranscriptPeek.Outcome.Ok && peek.Text != null)
                {
                    endsWithQuestion = TranscriptPeek.EndsWithQuestion(peek.Text);
                    messageTail = TranscriptPeek.TailOf(peek.Text);
                }
                else if (peek.Outcome == TranscriptPeek.Outcome.SchemaUnknown)
                {
                    MaybeToastSchemaBreak(hook.SessionId, peek.ParseError);
                }
                // FileMissing / NoAssistant: silently send a plain Stop with null fields.

                // Is the turn "finished" but with work still running? Two sources:
                //   1. background_tasks in the payload — run_in_background Bash shells (deterministic).
                //   2. a live Monitor watcher in the transcript — not in the payload (see HasLiveMonitor).
                // Either means DN should keep the desktop orange (working), not green (done).
                bool bashBackground = hook.BackgroundTasks is { ValueKind: JsonValueKind.Array } bt
                                      && bt.GetArrayLength() > 0;
                bool liveMonitor = TranscriptPeek.HasLiveMonitor(CacheDir, hook.SessionId, hook.TranscriptPath);
                backgroundActive = bashBackground || liveMonitor;
                if (backgroundActive)
                    Log($"  backgroundActive=true (bashTasks={bashBackground} liveMonitor={liveMonitor})");
            }

            // Build a human-readable tool description from the tool_input. PermissionRequest
            // fires right after PreToolUse for the same tool; without this it would clobber the
            // PreToolUse hover with a generic "Waiting for input". Same tool_input shape, so the
            // same builder applies — keeps the hover on e.g. Ask "Verify Channel".
            //
            // PostToolUse carries the same tool_input, and that matters beyond the hover:
            // PermissionRequest has no tool_use_id, so (toolName, toolDescription) is the only
            // handle DN has for deciding which completed tool answered which pending prompt.
            string? toolDescription = null;
            if (hook.HookEventName is "PreToolUse" or "PermissionRequest" or "PostToolUse")
            {
                toolDescription = BuildToolDescription(hook);
            }

            var payload = new StatePayload
            {
                Type             = "state",
                Source           = "claude-code",
                Title            = BuildTitle(hook.Cwd),
                Body             = BuildBody(internalKind, hook),
                SessionId        = TruncSession(hook.SessionId),
                Cwd              = hook.Cwd ?? "",
                TranscriptPath   = hook.TranscriptPath,
                Hostname         = Environment.MachineName,
                HookEvent        = hook.HookEventName,                  // authoritative classifier for DN
                HookSource       = hook.Source,
                ToolName         = hook.ToolName,
                ToolDescription  = toolDescription,
                Message          = hook.Message,
                NotificationKind = notificationKind,
                ErrorType        = hook.ErrorType ?? hook.Error ?? hook.Reason,
                VsCodePid        = vscodePid,
                SessionPid       = Environment.ProcessId,
                ParentPid        = LivenessPid(fromPid, walk.Chain),
                ParentChain      = walk.Chain,
                WalkOutcome      = walk.Outcome,
                LastMessageEndsWithQuestion = endsWithQuestion,
                LastMessageTail             = messageTail,
                BackgroundActive            = backgroundActive,
            };

            string outboundJson = JsonSerializer.Serialize(payload);
            Log($"OUT {outboundJson}");
            var reply = TrySendToDesktopNames(outboundJson);
            Log($"pipe delivered={reply.Ok}");

            // Emit the per-hook IDENTITY audit line and update the cumulative
            // audit JSON. This is what tells us "did we have enough information
            // to resolve this hook, and if not, what was missing?"
            try { EmitIdentitySnapshot(hook, walk, vscodePid, fromPid, reply); } catch { }

            if (reply.Ok) { Log("EXIT pipe-ok"); return 0; }

            // Fallback: balloon for user-facing kinds only (asking, ready). busy/idle silently drop.
            // A Stop with background work still running isn't "ready" — suppress its balloon so
            // the user isn't told "done" mid-watch (DN, when reachable, keeps it orange instead).
            if (internalKind is "asking" or "ready" && !(internalKind == "ready" && backgroundActive))
            {
                Log("balloon starting");
                ShowBalloon(internalKind, payload.Title, payload.Body);
                Log("balloon returned");
            }
            else
            {
                Log("EXIT pipe-failed-no-balloon");
            }

            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(CacheDir, "error.log"),
                    $"{DateTime.UtcNow:O} {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
                Log($"CRASH {ex.GetType().Name}: {ex.Message}");
            } catch { }
            return 0;
        }
    }

    // ---- hook stdin parsing -------------------------------------------------

    private sealed class HookInput
    {
        [JsonPropertyName("hook_event_name")] public string? HookEventName { get; set; }
        [JsonPropertyName("session_id")]      public string? SessionId { get; set; }
        [JsonPropertyName("cwd")]             public string? Cwd { get; set; }
        [JsonPropertyName("transcript_path")] public string? TranscriptPath { get; set; }
        [JsonPropertyName("message")]         public string? Message { get; set; }
        [JsonPropertyName("tool_name")]       public string? ToolName { get; set; }
        [JsonPropertyName("source")]          public string? Source { get; set; }   // SessionStart: startup/resume/clear
        [JsonPropertyName("agent_id")]        public string? AgentId { get; set; }   // set only on hooks fired from inside a subagent
        [JsonPropertyName("agent_type")]      public string? AgentType { get; set; } // e.g. "general-purpose", "Explore"
        [JsonPropertyName("tool_input")]      public JsonElement? ToolInput { get; set; }  // PreToolUse: raw tool input JSON
        [JsonPropertyName("tool_response")]   public JsonElement? ToolResponse { get; set; } // PostToolUse: what the tool returned

        // Stop only: run_in_background Bash shells still alive at turn end. Note this does
        // NOT include Monitor watchers (verified empirically) — those are detected via the
        // transcript. Present-and-non-empty means Claude finished the turn but work continues.
        [JsonPropertyName("background_tasks")] public JsonElement? BackgroundTasks { get; set; }

        // StopFailure: try multiple field names — Claude's stable payload shape isn't
        // fully documented; capture whichever one is present.
        [JsonPropertyName("error_type")]      public string? ErrorType { get; set; }
        [JsonPropertyName("error")]           public string? Error { get; set; }
        [JsonPropertyName("reason")]          public string? Reason { get; set; }
    }

    private static HookInput? ReadHookJsonFromStdin()
    {
        // Claude Code writes a single JSON object then closes stdin.
        // If stdin is a console (no redirection) Read returns immediately at EOF.
        if (Console.IsInputRedirected == false) return null;

        // Read stdin as raw bytes — Console.In can mis-encode on Windows
        // depending on chcp / OEM codepage. JSON is always UTF-8.
        using var ms = new MemoryStream();
        using (var stdin = Console.OpenStandardInput()) stdin.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        if (bytes.Length == 0) return null;

        // Strip UTF-8 BOM if present.
        int offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            offset = 3;

        string json = System.Text.Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        if (string.IsNullOrWhiteSpace(json)) return null;
        Log($"RAWIN {json}");   // Claude's exact stdin — the ground truth when Anthropic changes the payload shape.
        try
        {
            return JsonSerializer.Deserialize<HookInput>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    // ---- state mapping ------------------------------------------------------

    /// <summary>
    /// Internal-only state classifier used for two ClaudeHook-side decisions:
    ///   1. The 1Hz busy-event debounce (we coalesce all "Claude is working" events).
    ///   2. The Windows-balloon fallback (only fired for user-interruptible states).
    /// This mapping is NOT sent to DesktopNames any more — see the wire schema in
    /// DESKTOPNAMES-INTEGRATION.md. DN keys off `hookEvent` and decides colour itself.
    /// </summary>
    /// <summary>
    /// The long-lived pid DesktopNames watches to tell whether a session is still alive.
    /// claude-alert.sh passes $PPID, but under Git Bash that is MSYS pid 1 — not a real
    /// Windows process — so DN got no liveness signal at all and had to fall back to
    /// reaping silent sessions on a 15-minute timer. The walked chain already contains
    /// claude.exe, so use that instead whenever $PPID is unusable.
    /// </summary>
    private static int LivenessPid(int fromPid, List<ChainEntry> chain)
    {
        if (fromPid > 4) return fromPid;
        var claude = chain.FirstOrDefault(c => c.Name.Equals("claude", StringComparison.OrdinalIgnoreCase));
        return claude?.Pid ?? 0;
    }

    private static string InternalKind(string? evt) => evt switch
    {
        "SessionStart"      => "busy",
        "UserPromptSubmit"  => "busy",
        "PreToolUse"        => "busy",
        "PostToolUse"       => "busy",   // also DN's "the permission prompt was answered" signal
        "PermissionRequest" => "asking",  // inline y/n dialog — Claude is blocked on user
        "Notification"      => "asking",  // both permission_prompt and idle_prompt — user is being waited on
        "Stop"              => "ready",
        "StopFailure"       => "ready",   // turn ended (with an error); colour is DN's call
        "SessionEnd"        => "idle",
        _                   => "",
    };

    private static string BuildTitle(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "Claude";
        string leaf = Path.GetFileName(cwd.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(leaf) ? "Claude" : $"Claude — {leaf}";
    }

    /// <summary>
    /// Body text for the fallback Windows balloon (only fired when DN is unreachable).
    /// `kind` is the InternalKind classifier — NOT on the wire — driving toast text only.
    /// </summary>
    private static string BuildBody(string kind, HookInput hook)
    {
        if (kind == "busy")
            return !string.IsNullOrEmpty(hook.ToolName) ? $"Running {hook.ToolName}" : "Working";

        if (kind == "asking")
            return !string.IsNullOrEmpty(hook.Message)
                ? (hook.Message!.Length > 120 ? TranscriptPeek.HeadOf(hook.Message, 117) : hook.Message)
                : "Waiting for input";

        if (kind == "ready")
        {
            if (hook.HookEventName == "StopFailure")
            {
                string err = hook.ErrorType ?? hook.Error ?? hook.Reason ?? "error";
                return $"Stopped: {err}";
            }
            return "Ready";
        }

        return "";   // idle and unknown
    }

    /// <summary>
    /// Builds a human-readable description of what tool is running for PreToolUse events.
    /// Bash uses the user-provided `description` field; others extract the most identifying
    /// parameter (filename or pattern). Long values are truncated at word boundaries.
    /// </summary>
    private static string? BuildToolDescription(HookInput hook)
    {
        if (string.IsNullOrEmpty(hook.ToolName) || hook.ToolInput == null)
            return null;

        try
        {
            // Bash: user-written description is the best text.
            if (hook.ToolName == "Bash")
            {
                if (hook.ToolInput.Value.TryGetProperty("description", out var desc) &&
                    desc.ValueKind == JsonValueKind.String)
                {
                    string descStr = desc.GetString() ?? "";
                    return string.IsNullOrEmpty(descStr) ? null : TranscriptPeek.HeadOf(descStr, 100);
                }
                return null;
            }

            // Read: format as "Read {filename}"
            if (hook.ToolName == "Read")
            {
                if (hook.ToolInput.Value.TryGetProperty("file_path", out var path) &&
                    path.ValueKind == JsonValueKind.String)
                {
                    string pathStr = path.GetString() ?? "";
                    if (!string.IsNullOrEmpty(pathStr))
                        return $"Read {Path.GetFileName(pathStr)}";
                }
                return null;
            }

            // Grep: format as "Grep \"{pattern}\""
            if (hook.ToolName == "Grep")
            {
                if (hook.ToolInput.Value.TryGetProperty("pattern", out var pattern) &&
                    pattern.ValueKind == JsonValueKind.String)
                {
                    string patternStr = pattern.GetString() ?? "";
                    if (!string.IsNullOrEmpty(patternStr))
                    {
                        string truncated = TranscriptPeek.HeadOf(patternStr, 80);
                        return $"Grep \"{truncated}\"";
                    }
                }
                return null;
            }

            // Glob: format as "Glob {pattern}"
            if (hook.ToolName == "Glob")
            {
                if (hook.ToolInput.Value.TryGetProperty("pattern", out var globPattern) &&
                    globPattern.ValueKind == JsonValueKind.String)
                {
                    string globStr = globPattern.GetString() ?? "";
                    if (!string.IsNullOrEmpty(globStr))
                        return $"Glob {TranscriptPeek.HeadOf(globStr, 80)}";
                }
                return null;
            }

            // Edit/Write: format as "Edit {filename}" / "Write {filename}"
            if (hook.ToolName is "Edit" or "Write")
            {
                if (hook.ToolInput.Value.TryGetProperty("file_path", out var filePath) &&
                    filePath.ValueKind == JsonValueKind.String)
                {
                    string fileStr = filePath.GetString() ?? "";
                    if (!string.IsNullOrEmpty(fileStr))
                        return $"{hook.ToolName} {Path.GetFileName(fileStr)}";
                }
                return null;
            }

            // AskUserQuestion: prefer the short header ("Verify Channel"); fall back to
            // the full question text if a question somehow has no header.
            if (hook.ToolName == "AskUserQuestion")
            {
                if (hook.ToolInput.Value.TryGetProperty("questions", out var questions) &&
                    questions.ValueKind == JsonValueKind.Array &&
                    questions.GetArrayLength() > 0)
                {
                    var first = questions[0];
                    if (first.TryGetProperty("header", out var header) &&
                        header.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(header.GetString()))
                    {
                        return $"Ask \"{TranscriptPeek.HeadOf(header.GetString()!, 80)}\"";
                    }
                    if (first.TryGetProperty("question", out var question) &&
                        question.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(question.GetString()))
                    {
                        return $"Ask \"{TranscriptPeek.HeadOf(question.GetString()!, 80)}\"";
                    }
                }
                return null;
            }

            // Unrecognized tool: fall back to tool name.
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string TruncSession(string? id) =>
        string.IsNullOrEmpty(id) ? "" : id.Substring(0, Math.Min(8, id.Length));

    /// <summary>
    /// Classifies every identity field we use and emits an IDENTITY log line +
    /// updates the cumulative audit JSON. Lets us tell, across all hooks, where
    /// the gaps are (missing/bad data) vs where DesktopNames just couldn't
    /// resolve (resolver problem on the DN side, not a data problem here).
    /// </summary>
    private static void EmitIdentitySnapshot(HookInput hook, WalkResult walk,
        int vscodePid, int fromPid, PipeReply reply)
    {
        var snap = new IdentityAudit.Snapshot
        {
            SessionShort = TruncSession(hook.SessionId),
            Cwd_Raw      = hook.Cwd ?? "",
            EventName    = hook.HookEventName ?? "",
            State        = InternalKind(hook.HookEventName),
            PpidRaw      = fromPid,
            VsCodePidRaw = vscodePid,
            ChainLength  = walk.Chain.Count,
            WalkOutcome  = walk.Outcome,
            DnDesktop    = reply.DesktopIndex,
            DnError      = reply.Error,
        };

        snap.SessionId = string.IsNullOrEmpty(hook.SessionId) ? IdentityAudit.FieldStatus.Missing
                       : hook.SessionId.Length < 32          ? IdentityAudit.FieldStatus.Bad
                                                             : IdentityAudit.FieldStatus.Ok;

        // cwd is "ok" only if the directory actually exists. A stale cwd (e.g. user
        // deleted the folder while Claude was running) is "bad" — DN will have no
        // workspace to match against.
        snap.Cwd = string.IsNullOrEmpty(hook.Cwd) ? IdentityAudit.FieldStatus.Missing
                 : !Directory.Exists(hook.Cwd)   ? IdentityAudit.FieldStatus.Bad
                                                 : IdentityAudit.FieldStatus.Ok;

        // transcript_path is the canonical session identity. Should always be present
        // on a modern Claude Code. If it's missing the user may be on an old version.
        snap.TranscriptPath = string.IsNullOrEmpty(hook.TranscriptPath) ? IdentityAudit.FieldStatus.Missing
                            : !File.Exists(hook.TranscriptPath)       ? IdentityAudit.FieldStatus.Bad
                                                                       : IdentityAudit.FieldStatus.Ok;

        snap.HookEvent = string.IsNullOrEmpty(hook.HookEventName) ? IdentityAudit.FieldStatus.Missing
                                                                  : IdentityAudit.FieldStatus.Ok;

        // tool_name is expected on PreToolUse / PostToolUse / PermissionRequest / PermissionDenied
        // (the last two carry the tool name in the matcher). n/a everywhere else.
        snap.ToolName = hook.HookEventName switch
        {
            "PreToolUse" or "PostToolUse" or "PermissionRequest" or "PermissionDenied"
                => string.IsNullOrEmpty(hook.ToolName)
                       ? IdentityAudit.FieldStatus.Missing
                       : IdentityAudit.FieldStatus.Ok,
            _   => IdentityAudit.FieldStatus.NotApplicable,
        };

        // message is only expected on Notification — mark n/a otherwise.
        snap.Message = hook.HookEventName switch
        {
            "Notification" => string.IsNullOrEmpty(hook.Message)
                                ? IdentityAudit.FieldStatus.Missing
                                : IdentityAudit.FieldStatus.Ok,
            _ => IdentityAudit.FieldStatus.NotApplicable,
        };

        // ppid: 0 = bash didn't pass it, 1 = MSYS quirk (treated as bad), >1 = ok.
        snap.Ppid = fromPid <= 0 ? IdentityAudit.FieldStatus.Missing
                  : fromPid == 1 ? IdentityAudit.FieldStatus.Bad
                                 : IdentityAudit.FieldStatus.Ok;

        // chain: empty → missing; reached Code → ok; otherwise partial.
        snap.ParentChain = walk.Chain.Count == 0 ? IdentityAudit.FieldStatus.Missing
                         : walk.VsCodePid > 0   ? IdentityAudit.FieldStatus.Ok
                                                 : IdentityAudit.FieldStatus.Partial;

        snap.VsCodePid = vscodePid > 0 ? IdentityAudit.FieldStatus.Ok
                                       : IdentityAudit.FieldStatus.Missing;

        snap.DnResolution = reply.Ok ? IdentityAudit.FieldStatus.Ok
                                     : IdentityAudit.FieldStatus.Bad;

        IdentityAudit.Emit(snap, Log);
    }

    // ---- one-shot schema-break toast ---------------------------------------

    /// <summary>
    /// Called when TranscriptPeek can't parse the file with our expected schema.
    /// First time per session, writes SCHEMA_CHANGED to the log and pops a Windows
    /// toast so the user knows the question-detector is offline until ClaudeHook
    /// is updated. Subsequent calls in the same session no-op.
    /// </summary>
    private static void MaybeToastSchemaBreak(string? sessionId, string? parseError)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            Log($"SCHEMA_CHANGED transcript parse failed (no sessionId to dedupe): {parseError}");
            return;
        }

        string flagPath = Path.Combine(CacheDir, $"schema-warned-{sessionId}.flag");
        if (File.Exists(flagPath))
        {
            // Already warned for this session — log it quietly and return.
            Log($"SCHEMA_CHANGED transcript parse failed (already warned for this session): {parseError}");
            return;
        }

        try { File.WriteAllText(flagPath, ""); } catch { }
        Log($"SCHEMA_CHANGED transcript parse failed: {parseError} — popping one-shot toast");

        try
        {
            ShowBalloonExternal(
                state:     "asking",   // warning icon
                title:     "ClaudeHook needs update",
                body:      "Claude's transcript format changed — question detector offline.\n" +
                           "The 'Stop with question' yellow won't fire until ClaudeHook is updated.\n" +
                           $"({parseError})",
                displayMs: 8000);
        }
        catch (Exception ex) { Log($"  toast failed: {ex.Message}"); }
    }

    // ---- 1Hz busy debounce per session --------------------------------------

    /// <summary>
    /// Is a permission prompt outstanding for this session? Raised by PermissionRequest /
    /// Notification, lowered by the PostToolUse that answers it or by the end of the turn.
    /// Unknown session or unreadable flag answers true, so an error can only cost us the
    /// trim, never the prompt-answered signal.
    /// </summary>
    private static bool HasPendingAsk(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return true;
        try { return File.Exists(Path.Combine(CacheDir, $"ask-{sessionId}.flag")); }
        catch { return true; }
    }

    private static void SetPendingAsk(string? sessionId, bool pending)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try
        {
            string path = Path.Combine(CacheDir, $"ask-{sessionId}.flag");
            if (pending) File.WriteAllText(path, "");
            else File.Delete(path);
        }
        catch { }
    }

    private static bool IsBusyDebounced(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        string path = Path.Combine(CacheDir, $"busy-{sessionId}.stamp");
        try
        {
            if (File.Exists(path))
            {
                var last = File.GetLastWriteTimeUtc(path);
                if ((DateTime.UtcNow - last).TotalMilliseconds < BusyDebounceMs)
                    return true;
            }
            File.WriteAllText(path, "");
            return false;
        }
        catch
        {
            // If we can't stamp, don't debounce — better to spam than to lose state.
            return false;
        }
    }

    // ---- pipe transport -----------------------------------------------------

    /// <summary>
    /// Wire payload to DesktopNames. ClaudeHook's contract is "report every fact
    /// I can scrape, you decide". DesktopNames owns resolution AND colour mapping.
    ///
    /// The wire used to carry a `state` enum (busy/asking/ready/idle) that conflated
    /// distinct Claude events. We dropped it 2026-05-20 — DN now keys on `hookEvent`
    /// directly and maps each event to its own colour. See DESKTOPNAMES-INTEGRATION.md
    /// for the colour-mapping table.
    ///
    /// Extra fields beyond what DN currently uses are silently ignored by
    /// System.Text.Json — safe to ship ahead of DN updates.
    /// </summary>
    private sealed class StatePayload
    {
        // --- core ---
        [JsonPropertyName("type")]       public string Type { get; set; } = "";
        [JsonPropertyName("source")]     public string Source { get; set; } = "";
        [JsonPropertyName("title")]      public string Title { get; set; } = "";
        [JsonPropertyName("body")]       public string Body { get; set; } = "";

        // --- identity ---
        [JsonPropertyName("sessionId")]      public string  SessionId { get; set; } = "";
        [JsonPropertyName("cwd")]            public string  Cwd { get; set; } = "";
        [JsonPropertyName("transcriptPath")] public string? TranscriptPath { get; set; }
        [JsonPropertyName("hostname")]       public string  Hostname { get; set; } = "";

        // --- hook context ---
        [JsonPropertyName("hookEvent")]        public string? HookEvent { get; set; }
        [JsonPropertyName("hookSource")]       public string? HookSource { get; set; }       // SessionStart: startup/resume/clear
        [JsonPropertyName("toolName")]         public string? ToolName { get; set; }
        [JsonPropertyName("toolDescription")]  public string? ToolDescription { get; set; } // PreToolUse: human-readable tool action (e.g. "Grep \"pattern\"")
        [JsonPropertyName("message")]          public string? Message { get; set; }
        [JsonPropertyName("notificationKind")] public string? NotificationKind { get; set; } // permission_prompt | idle_prompt | auth_success
        [JsonPropertyName("errorType")]        public string? ErrorType { get; set; }        // StopFailure: rate_limit | authentication_failed | billing_error | ...

        // Stop only: peek of Claude's last assistant text so DN can colour-distinguish
        // "Claude finished" from "Claude finished AND ended with a question". Populated by
        // TranscriptPeek; both fields null if the transcript is missing/unparseable.
        [JsonPropertyName("lastMessageEndsWithQuestion")] public bool? LastMessageEndsWithQuestion { get; set; }
        [JsonPropertyName("lastMessageTail")]              public string? LastMessageTail { get; set; }

        // Stop only: true when the turn ended but background work is still running — a live
        // Monitor watcher, or a run_in_background Bash shell. DN colours such a Stop orange
        // (working) instead of green (done). See DESKTOPNAMES-INTEGRATION.md.
        [JsonPropertyName("backgroundActive")]             public bool BackgroundActive { get; set; }

        // --- process tree (everything we know; DN matches however it wants) ---
        [JsonPropertyName("vscodePid")]      public int                  VsCodePid { get; set; }    // first Code/Cursor in chain, or 0
        [JsonPropertyName("sessionPid")]     public int                  SessionPid { get; set; }   // this exe's PID
        [JsonPropertyName("parentPid")]      public int                  ParentPid { get; set; }    // claude.exe PID we walked from
        [JsonPropertyName("parentChain")]    public List<ChainEntry>     ParentChain { get; set; } = new();
        [JsonPropertyName("walkOutcome")]    public string               WalkOutcome { get; set; } = "";
    }

    /// <summary>One entry in the parent-process chain. DN can match by any name.</summary>
    internal sealed class ChainEntry
    {
        [JsonPropertyName("pid")]  public int    Pid { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    /// <summary>What we got back from DesktopNames. Drives both the balloon fallback
    /// decision and the IDENTITY audit line.</summary>
    internal sealed class PipeReply
    {
        public bool PipeReachable { get; set; }       // pipe connected at all
        public bool Ok { get; set; }                  // DN replied with ok:true
        public int  DesktopIndex { get; set; } = -1;
        public string DesktopName { get; set; } = "";
        public string Error { get; set; } = "";       // DN's error code on ok:false
        public string? UserMessage { get; set; }      // optional warning text DN may include
    }

    private static PipeReply TrySendToDesktopNames(string json)
    {
        var sw = Stopwatch.StartNew();
        var reply = new PipeReply();
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName,
                PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(PipeConnectTimeoutMs);
            reply.PipeReachable = true;
            Log($"  pipe connected in {sw.ElapsedMilliseconds}ms");

            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(json);

            using var reader = new StreamReader(pipe, leaveOpen: true);
            string? rawReply = reader.ReadLine();
            Log($"  pipe reply='{rawReply}' total={sw.ElapsedMilliseconds}ms");
            if (string.IsNullOrEmpty(rawReply)) return reply;

            try
            {
                using var doc = JsonDocument.Parse(rawReply);
                var r = doc.RootElement;
                reply.Ok = r.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                if (reply.Ok)
                {
                    if (r.TryGetProperty("desktopIndex", out var di) && di.ValueKind == JsonValueKind.Number) reply.DesktopIndex = di.GetInt32();
                    if (r.TryGetProperty("desktopName",  out var dn) && dn.ValueKind == JsonValueKind.String) reply.DesktopName  = dn.GetString() ?? "";
                    Log($"  RESOLVED desktop={reply.DesktopIndex} name='{reply.DesktopName}' (DesktopNames decided this from the payload above)");
                }
                else
                {
                    if (r.TryGetProperty("error",       out var er) && er.ValueKind == JsonValueKind.String) reply.Error       = er.GetString() ?? "";
                    if (r.TryGetProperty("userMessage", out var um) && um.ValueKind == JsonValueKind.String) reply.UserMessage = um.GetString();
                    Log($"  UNRESOLVED error='{reply.Error}' (DesktopNames could not match the payload to a desktop)");
                    if (reply.UserMessage != null) Log($"  DN userMessage='{reply.UserMessage}'");
                }
            }
            catch (Exception ex) { Log($"  reply parse failed: {ex.Message}"); }

            return reply;
        }
        catch (TimeoutException)
        {
            Log($"  pipe TimeoutException at {sw.ElapsedMilliseconds}ms");
            return reply;
        }
        catch (IOException ex)
        {
            Log($"  pipe IOException at {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return reply;
        }
        catch (Exception ex)
        {
            Log($"  pipe {ex.GetType().Name} at {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return reply;
        }
    }

    // ---- balloon fallback ---------------------------------------------------

    private static void ShowBalloon(string state, string title, string body) =>
        BalloonImpl.Show(state, title, body, 10000);

    internal static void ShowBalloonExternal(string state, string title, string body, int displayMs = 4000) =>
        BalloonImpl.Show(state, title, body, displayMs);

    internal static class BalloonImpl
    {
        public static void Show(string state, string title, string body, int displayMs)
        {
            using var icon = new System.Windows.Forms.NotifyIcon();
            icon.Icon = state == "asking"
                ? System.Drawing.SystemIcons.Warning
                : System.Drawing.SystemIcons.Information;
            icon.Visible = true;
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = body;
            icon.BalloonTipIcon = state == "asking"
                ? System.Windows.Forms.ToolTipIcon.Warning
                : System.Windows.Forms.ToolTipIcon.Info;
            icon.ShowBalloonTip(displayMs);

            // The balloon needs a message pump to paint. A tiny hidden form + timer
            // is the established pattern — see git history of claude-alert.ps1 for the
            // earlier DoEvents-based attempt that didn't paint reliably.
            using var form = new System.Windows.Forms.Form
            {
                ShowInTaskbar = false,
                WindowState   = System.Windows.Forms.FormWindowState.Minimized,
                Opacity       = 0,
            };
            using var timer = new System.Windows.Forms.Timer { Interval = displayMs };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                icon.Visible = false;
                form.Close();
            };
            form.Shown += (_, _) => timer.Start();
            System.Windows.Forms.Application.Run(form);
        }
    }
}
