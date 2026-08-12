using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHook;

/// <summary>
/// For each hook event we summarise what identity material we actually got and
/// what was missing. The point is to make it easy to answer two questions just
/// by reading the log:
///
///   1. Across all hooks, what fraction of the identity information we *could*
///      have arrived 100% of the time?
///   2. When something is missing, is it user-fixable (open the cwd in VSCode,
///      upgrade Claude Code) or is it a tool bug (we should be capturing X but
///      we aren't)?
///
/// Side effect: a running %TEMP%/claude-alert/identity-audit.json that
/// accumulates per-session and per-field counters so the picture is queryable
/// without grepping 1000s of log lines.
/// </summary>
internal static class IdentityAudit
{
    private static readonly string AuditPath =
        Path.Combine(Path.GetTempPath(), "claude-alert", "identity-audit.json");

    /// <summary>Status of one identity field. ok = we got it and it looks usable.</summary>
    public enum FieldStatus
    {
        Ok,           // present, looks real
        Missing,      // not in the hook payload at all
        Bad,          // present but malformed / doesn't reference anything real
        Partial,      // got some of it (chain only — some hops succeeded, some didn't)
        NotApplicable // expected to be absent for this event type
    }

    /// <summary>Per-hook snapshot. Written to log and aggregated into the audit JSON.</summary>
    public sealed class Snapshot
    {
        // Identity inputs from the hook JSON.
        public FieldStatus SessionId      { get; set; }
        public FieldStatus Cwd            { get; set; }
        public FieldStatus TranscriptPath { get; set; }
        public FieldStatus HookEvent      { get; set; }
        public FieldStatus ToolName       { get; set; }  // expected on PreToolUse only
        public FieldStatus Message        { get; set; }  // expected on Notification only

        // From the bash wrapper / parent-walk.
        public FieldStatus Ppid           { get; set; }
        public FieldStatus ParentChain    { get; set; }
        public FieldStatus VsCodePid      { get; set; }

        // From DesktopNames' reply.
        public FieldStatus DnResolution   { get; set; }

        // Raw values for the log line (truncated).
        public string SessionShort    { get; set; } = "";
        public string Cwd_Raw         { get; set; } = "";
        public string EventName       { get; set; } = "";   // raw hook_event_name (drives stuck detection)
        public string State           { get; set; } = "";   // mapped state (busy/asking/ready/idle)
        public int    PpidRaw         { get; set; }
        public int    VsCodePidRaw    { get; set; }
        public int    ChainLength     { get; set; }
        public string WalkOutcome     { get; set; } = "";
        public string DnError         { get; set; } = "";
        public int    DnDesktop       { get; set; } = -1;
    }

    /// <summary>
    /// One concise line per hook. Encoded so it's easy to grep:
    ///   IDENTITY result=ok|fail session=... cwd=ok|miss|bad transcript=ok|miss ...
    /// </summary>
    public static void Emit(Snapshot s, Action<string> log)
    {
        _stuckLogger = log;
        string result = s.DnResolution == FieldStatus.Ok ? "ok" : "fail";
        var sb = new System.Text.StringBuilder();
        sb.Append($"IDENTITY result={result}");
        sb.Append($" session=").Append(s.SessionShort.Length > 0 ? s.SessionShort : "MISSING");
        sb.Append($" cwd=").Append(Tag(s.Cwd)).Append(s.Cwd == FieldStatus.Ok ? $"({s.Cwd_Raw})" : "");
        sb.Append($" transcript=").Append(Tag(s.TranscriptPath));
        sb.Append($" event=").Append(Tag(s.HookEvent));
        sb.Append($" tool=").Append(Tag(s.ToolName));
        sb.Append($" message=").Append(Tag(s.Message));
        sb.Append($" ppid=").Append(Tag(s.Ppid)).Append(s.PpidRaw > 0 ? $"({s.PpidRaw})" : $"({s.PpidRaw})");
        sb.Append($" chain=").Append(Tag(s.ParentChain)).Append($"({s.ChainLength})");
        sb.Append($" vscodePid=").Append(s.VsCodePidRaw);
        sb.Append($" walkOutcome={s.WalkOutcome}");
        if (s.DnResolution == FieldStatus.Ok) sb.Append($" -> desktop={s.DnDesktop}");
        else                                  sb.Append($" -> error='{s.DnError}'");
        log(sb.ToString());

        // Aggregate into the audit file. Best-effort — failures here are silent.
        try { UpdateAudit(s); } catch { }
    }

    private static string Tag(FieldStatus f) => f switch
    {
        FieldStatus.Ok            => "ok",
        FieldStatus.Missing       => "miss",
        FieldStatus.Bad           => "bad",
        FieldStatus.Partial       => "part",
        FieldStatus.NotApplicable => "n/a",
        _                          => "?",
    };

    // ---- aggregate JSON -----------------------------------------------------

    private sealed class AuditFile
    {
        [JsonPropertyName("totalHooks")]      public int TotalHooks { get; set; }
        [JsonPropertyName("resolvedHooks")]   public int ResolvedHooks { get; set; }
        [JsonPropertyName("unresolvedHooks")] public int UnresolvedHooks { get; set; }
        [JsonPropertyName("firstSeen")]       public string? FirstSeen { get; set; }
        [JsonPropertyName("lastUpdated")]     public string? LastUpdated { get; set; }

        /// <summary>How many hooks had each field in each state.</summary>
        [JsonPropertyName("fieldCounts")]
        public Dictionary<string, Dictionary<string, int>> FieldCounts { get; set; } = new();

        /// <summary>Per-session: how often it resolves, what's typically missing.</summary>
        [JsonPropertyName("sessions")]
        public Dictionary<string, SessionStat> Sessions { get; set; } = new();
    }

    private sealed class SessionStat
    {
        [JsonPropertyName("hooks")]            public int Hooks { get; set; }
        [JsonPropertyName("resolved")]         public int Resolved { get; set; }
        [JsonPropertyName("unresolved")]       public int Unresolved { get; set; }
        [JsonPropertyName("cwd")]              public string Cwd { get; set; } = "";
        [JsonPropertyName("lastError")]        public string? LastError { get; set; }
        [JsonPropertyName("lastEvent")]        public string? LastEvent { get; set; }
        [JsonPropertyName("lastEventTime")]    public string? LastEventTime { get; set; }
        [JsonPropertyName("lastTerminalTime")] public string? LastTerminalTime { get; set; }   // Stop / StopFailure / SessionEnd
        [JsonPropertyName("busyHooksSinceTerminal")] public int BusyHooksSinceTerminal { get; set; }
        [JsonPropertyName("stuckWarned")]      public bool StuckWarned { get; set; }
    }

    private static void UpdateAudit(Snapshot s)
    {
        AuditFile audit;
        if (File.Exists(AuditPath))
        {
            try { audit = JsonSerializer.Deserialize<AuditFile>(File.ReadAllText(AuditPath)) ?? new(); }
            catch { audit = new(); }
        }
        else
        {
            audit = new();
        }

        string now = DateTime.UtcNow.ToString("O");
        audit.FirstSeen ??= now;
        audit.LastUpdated = now;
        audit.TotalHooks++;
        if (s.DnResolution == FieldStatus.Ok) audit.ResolvedHooks++;
        else                                  audit.UnresolvedHooks++;

        // Field counters: per (fieldName, status) -> count.
        void Bump(string field, FieldStatus status)
        {
            if (status == FieldStatus.NotApplicable) return;   // don't pollute counts
            if (!audit.FieldCounts.TryGetValue(field, out var inner))
            {
                inner = new Dictionary<string, int>();
                audit.FieldCounts[field] = inner;
            }
            string key = Tag(status);
            inner[key] = inner.TryGetValue(key, out var v) ? v + 1 : 1;
        }
        Bump("sessionId",      s.SessionId);
        Bump("cwd",            s.Cwd);
        Bump("transcriptPath", s.TranscriptPath);
        Bump("hookEvent",      s.HookEvent);
        Bump("toolName",       s.ToolName);
        Bump("message",        s.Message);
        Bump("ppid",           s.Ppid);
        Bump("parentChain",    s.ParentChain);
        Bump("vscodePid",      s.VsCodePid);
        Bump("dnResolution",   s.DnResolution);

        // Per-session.
        if (!string.IsNullOrEmpty(s.SessionShort))
        {
            if (!audit.Sessions.TryGetValue(s.SessionShort, out var ss))
            {
                ss = new SessionStat { Cwd = s.Cwd_Raw };
                audit.Sessions[s.SessionShort] = ss;
            }
            ss.Hooks++;
            ss.LastEvent = s.EventName;
            ss.LastEventTime = now;
            if (s.DnResolution == FieldStatus.Ok) ss.Resolved++;
            else { ss.Unresolved++; ss.LastError = s.DnError; }

            // Fix D — stuck-session detection. Count busy events since the last
            // terminal event (Stop / StopFailure / SessionEnd). If a session sees
            // many busy events without a terminal, hooks probably got stripped or
            // Claude crashed — neither user nor downstream tool gets a clean signal.
            bool isTerminal = s.EventName is "Stop" or "StopFailure" or "SessionEnd";
            if (isTerminal)
            {
                ss.LastTerminalTime = now;
                ss.BusyHooksSinceTerminal = 0;
                ss.StuckWarned = false;
            }
            else if (s.State == "busy")
            {
                ss.BusyHooksSinceTerminal++;
                // Threshold: 20 busy events without a terminal. At 1Hz debounce that's
                // ~20 minutes of continuous activity — well past any normal turn.
                if (ss.BusyHooksSinceTerminal > 20 && !ss.StuckWarned)
                {
                    ss.StuckWarned = true;
                    // Write the warning into the same shared log via callback. Caller
                    // owns the log writer so we don't duplicate path logic here.
                    _stuckLogger?.Invoke(
                        $"STUCK_SESSION session={s.SessionShort} cwd={s.Cwd_Raw} " +
                        $"busyHooks={ss.BusyHooksSinceTerminal} lastTerminal={ss.LastTerminalTime ?? "never"} " +
                        $"(hooks stripped, Claude crashed, or Stop/StopFailure not subscribed?)");
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(AuditPath)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(AuditPath, JsonSerializer.Serialize(audit, opts));
    }

    /// <summary>
    /// Set by Emit() each call so UpdateAudit can write stuck-session warnings
    /// into the shared log. Keeping it as a field avoids threading a logger
    /// through every method signature.
    /// </summary>
    private static Action<string>? _stuckLogger;
}
