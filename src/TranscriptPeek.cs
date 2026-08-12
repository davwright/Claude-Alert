using System.Text.Json;

namespace ClaudeHook;

/// <summary>
/// Reads the tail of a Claude Code transcript JSONL file to extract the
/// assistant's most recent text content. Used by the Stop hook path so that
/// DesktopNames can colour-distinguish "Claude finished a turn" from "Claude
/// finished a turn AND the last sentence is a question".
///
/// Schema awareness: the Claude Code transcript format is not a stable published
/// contract. If parsing fails or the expected shape is gone, we return a
/// SchemaUnknown result and the caller (a) sends a plain Stop without the
/// question fields, (b) pops a one-shot Windows toast asking the user to
/// notify Claude. We never silently degrade with stale data.
///
/// Schema expected:
///   one JSON object per line; assistant entries look like
///     { "type": "assistant",
///       "message": { "role": "assistant",
///                    "content": [ { "type": "text", "text": "..." },
///                                  { "type": "tool_use", ... } ] },
///       ... }
///   We scan back from EOF, find the most recent assistant entry whose content
///   has a "text" item, and return that text.
/// </summary>
internal static class TranscriptPeek
{
    public enum Outcome
    {
        Ok,             // we got the assistant's last text
        NoAssistant,    // file exists, parses, but no assistant text in tail — empty session?
        FileMissing,    // transcriptPath doesn't exist on disk
        SchemaUnknown,  // file exists but the shape is unrecognised — toast the user
    }

    public sealed class Result
    {
        public Outcome Outcome { get; init; }
        public string? Text { get; init; }
        public string? ParseError { get; init; }
    }

    /// <summary>Look at the last ~30 lines for the most recent assistant text.</summary>
    private const int MaxLinesToScan = 30;

    /// <summary>
    /// Window for the monitor terminal-evidence scan. This no longer bounds correctness:
    /// arms come from the sidecar (see <see cref="ArmMonitor"/>), so a monitor whose arm
    /// has scrolled far out of reach is still known. The scan only looks for the
    /// completion of an id we already know about; missing it costs a late clear, which
    /// the declared-timeout expiry then bounds.
    /// </summary>
    private const int MaxLinesForMonitorScan = 400;

    /// <summary>Grace added to a monitor's own declared timeout before we consider a
    /// still-armed entry dead. Covers the gap between the watcher expiring and its final
    /// notification landing.</summary>
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromMinutes(1);

    /// <summary>Max length we'll return as the "tail" of the last message.</summary>
    public const int MaxTailLength = 200;

    /// <summary>
    /// Completion tokens that mark a monitor's final task-notification. The first is
    /// harness-generated and fully reliable; the rest are author-chosen sentinels seen
    /// across real monitor scripts (build watches, UI-test runs, sandbox e2e). Matching
    /// is case-insensitive. Erring toward "not terminal" keeps the desktop busy a little
    /// too long rather than falsely green — the direction the user wants.
    /// </summary>
    private static readonly string[] MonitorDoneTokens =
    {
        "[monitor timed out",   // harness-generated — reliable
        "completed", "succeeded", "-done", "settled", "---final---",
        "failed", "fatal", "timed out", "closure complete",
    };

    public static Result Read(string? transcriptPath)
    {
        if (string.IsNullOrEmpty(transcriptPath))
            return new Result { Outcome = Outcome.FileMissing };
        if (!File.Exists(transcriptPath))
            return new Result { Outcome = Outcome.FileMissing };

        try
        {
            // Read the file's tail. The transcript is append-only and can be MB-large,
            // so we don't load the whole thing. Use File.ReadLines + tail-pick semantics.
            var tail = ReadLastLines(transcriptPath, MaxLinesToScan);
            string? assistantText = null;
            bool sawAtLeastOneParseableLine = false;
            bool sawAssistantStructure = false;

            // Walk in chronological order (oldest of the tail first) and remember the
            // most recent assistant TEXT we find. Tool-use entries are common in the
            // tail; we skip those.
            foreach (string line in tail)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(line); }
                catch { continue; }   // skip un-parseable lines, they happen at truncation boundaries
                using (doc)
                {
                    sawAtLeastOneParseableLine = true;
                    var root = doc.RootElement;

                    // Schema check 1: top-level should be an object with a "type" string.
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                        continue;

                    if (typeEl.GetString() != "assistant") continue;

                    // Schema check 2: assistant.message.content[*].type == "text"
                    if (!root.TryGetProperty("message", out var messageEl) ||
                        messageEl.ValueKind != JsonValueKind.Object) continue;
                    if (!messageEl.TryGetProperty("content", out var contentEl) ||
                        contentEl.ValueKind != JsonValueKind.Array) continue;

                    sawAssistantStructure = true;

                    foreach (var item in contentEl.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (!item.TryGetProperty("type", out var itemType)) continue;
                        if (itemType.GetString() != "text") continue;
                        if (!item.TryGetProperty("text", out var textEl)) continue;
                        if (textEl.ValueKind != JsonValueKind.String) continue;
                        var t = textEl.GetString();
                        if (!string.IsNullOrEmpty(t)) assistantText = t;
                    }
                }
            }

            // If nothing parsed at all, the file shape is fully unknown — treat as schema break.
            if (!sawAtLeastOneParseableLine)
                return new Result { Outcome = Outcome.SchemaUnknown,
                                     ParseError = "no parseable lines in tail" };

            // Parsed lines but never saw the expected assistant shape (and no text found).
            // Could be a session of all-tool-use turns, or could be a schema break. Lean
            // toward "no assistant" (less alarming) unless we never saw the assistant
            // type at all — that's a stronger signal of schema drift.
            if (assistantText == null && !sawAssistantStructure)
                return new Result { Outcome = Outcome.SchemaUnknown,
                                     ParseError = "no assistant.message.content array seen" };

            if (assistantText == null)
                return new Result { Outcome = Outcome.NoAssistant };

            return new Result { Outcome = Outcome.Ok, Text = assistantText };
        }
        catch (Exception ex)
        {
            return new Result { Outcome = Outcome.SchemaUnknown, ParseError = ex.Message };
        }
    }

    /// <summary>
    /// Records that a Monitor was armed, from the PostToolUse tool_response text
    /// ("Monitor started (task bc66xiqcd, timeout 70000ms)."). Called on every
    /// PostToolUse for the Monitor tool; a response without that marker is ignored.
    ///
    /// This is the fix for the window problem: the arm is remembered in a sidecar
    /// keyed by session, so it survives however much transcript is written afterwards.
    /// The transcript is then only consulted for the *completion* of an id we already
    /// know about (see <see cref="HasLiveMonitor"/>).
    /// </summary>
    public static void ArmMonitor(string cacheDir, string? sessionId, string? toolResponse)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(toolResponse)) return;
        string? id = ExtractAfter(toolResponse!, "Monitor started (task ", ',');
        if (id == null) return;

        // "…, timeout 600000ms)" — the monitor's own declared lifetime, used as the
        // backstop so a watcher that dies without a final notification can't pin the
        // session busy forever.
        long timeoutMs = 0;
        string? t = ExtractAfter(toolResponse!, "timeout ", 'm');
        if (t != null) long.TryParse(t.Trim(), out timeoutMs);

        try
        {
            var armed = LoadArmed(cacheDir, sessionId!);
            armed[id] = new ArmedMonitor { ArmedUtc = DateTime.UtcNow, TimeoutMs = timeoutMs };
            SaveArmed(cacheDir, sessionId!, armed);
        }
        catch { /* liveness is best-effort; never break the hook */ }
    }

    /// <summary>Drop a session's armed-monitor sidecar (SessionEnd).</summary>
    public static void ClearMonitors(string cacheDir, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try { File.Delete(ArmedPath(cacheDir, sessionId!)); } catch { }
    }

    /// <summary>
    /// True when a Monitor watcher armed by this session is still running at this Stop.
    ///
    /// Why this exists: a Monitor tool call returns immediately ("Keep working"), so the
    /// turn ends and Stop fires — painting the desktop green — while the watcher runs on
    /// in the background. Monitors are NOT in the Stop payload's `background_tasks` array
    /// (that only tracks run_in_background Bash), so they need their own signal.
    ///
    /// An id is dropped when any of these says it's over:
    ///   - a terminal task-notification in the transcript tail (<c>&lt;status&gt;</c> or
    ///     <c>&lt;event&gt;</c> matching <see cref="MonitorDoneTokens"/>),
    ///   - its own declared timeout plus <see cref="ExpiryGrace"/> has elapsed.
    /// Terminal evidence is sticky: a monitor that reported completion does not resume,
    /// and a re-armed watcher gets a fresh harness id (and a fresh sidecar entry).
    /// </summary>
    public static bool HasLiveMonitor(string cacheDir, string? sessionId, string? transcriptPath)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;

        try
        {
            var armed = LoadArmed(cacheDir, sessionId!);
            if (armed.Count == 0) return false;

            var now = DateTime.UtcNow;
            foreach (var (id, m) in armed.ToList())
            {
                if (m.TimeoutMs > 0 &&
                    now - m.ArmedUtc > TimeSpan.FromMilliseconds(m.TimeoutMs) + ExpiryGrace)
                    armed.Remove(id);
            }

            if (armed.Count > 0)
                foreach (string id in TerminalMonitorIds(transcriptPath, armed.Keys))
                    armed.Remove(id);

            SaveArmed(cacheDir, sessionId!, armed);
            return armed.Count > 0;
        }
        catch { return false; }   // never let this break the Stop path
    }

    /// <summary>
    /// Ids among <paramref name="ofInterest"/> whose task-notifications in the transcript
    /// tail show a completion. Only ids we already know were armed are considered — a
    /// run_in_background Bash task emits the same <c>&lt;task-notification&gt;</c> wrapper.
    /// </summary>
    private static IEnumerable<string> TerminalMonitorIds(string? transcriptPath, IEnumerable<string> ofInterest)
    {
        var wanted = new HashSet<string>(ofInterest, StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) return done;

        foreach (string line in ReadLastLines(transcriptPath!, MaxLinesForMonitorScan))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.Contains("task-notification", StringComparison.Ordinal)) continue;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                foreach (string content in MonitorTexts(doc.RootElement))
                {
                    string? id = ExtractBetween(content, "<task-id>", "</task-id>");
                    if (id == null || !wanted.Contains(id) || done.Contains(id)) continue;

                    // Two shapes carry the outcome: the task wrapper's completion
                    // notification (<status>completed</status>, no <event>) and a monitor's
                    // own event text. Either counts; neither present means this notification
                    // simply carries no liveness information.
                    string? text = ExtractBetween(content, "<status>", "</status>")
                                ?? ExtractBetween(content, "<event>", "</event>");
                    if (text == null) continue;
                    if (MonitorDoneTokens.Any(tok => text.Contains(tok, StringComparison.OrdinalIgnoreCase)))
                        done.Add(id);
                }
            }
        }
        return done;
    }

    private sealed class ArmedMonitor
    {
        public DateTime ArmedUtc { get; set; }
        public long TimeoutMs { get; set; }
    }

    private static string ArmedPath(string cacheDir, string sessionId) =>
        Path.Combine(cacheDir, $"monitors-{sessionId}.json");

    private static Dictionary<string, ArmedMonitor> LoadArmed(string cacheDir, string sessionId)
    {
        string path = ArmedPath(cacheDir, sessionId);
        if (!File.Exists(path)) return new Dictionary<string, ArmedMonitor>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ArmedMonitor>>(File.ReadAllText(path))
                   ?? new Dictionary<string, ArmedMonitor>(StringComparer.Ordinal);
        }
        catch { return new Dictionary<string, ArmedMonitor>(StringComparer.Ordinal); }
    }

    private static void SaveArmed(string cacheDir, string sessionId, Dictionary<string, ArmedMonitor> armed)
    {
        string path = ArmedPath(cacheDir, sessionId);
        try
        {
            if (armed.Count == 0) { File.Delete(path); return; }
            File.WriteAllText(path, JsonSerializer.Serialize(armed));
        }
        catch { }
    }

    /// <summary>
    /// Yields the monitor-relevant text from a transcript line. Two shapes carry it:
    ///   - the Monitor ARM lands as a tool_result inside a type:"user" message.content[]
    ///     ("Monitor started (task ...)"),
    ///   - each monitor EVENT lands as a type:"queue-operation" line with a top-level
    ///     `content` string holding the &lt;task-notification&gt; markup.
    /// A tool_result is used for the arm specifically so that assistant text merely quoting
    /// "Monitor started (task ...)" (as this very file's narration does) never false-arms.
    /// </summary>
    private static IEnumerable<string> MonitorTexts(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;

        // queue-operation: the event notification, text in a top-level `content` string.
        if (root.TryGetProperty("type", out var topType) &&
            topType.ValueKind == JsonValueKind.String &&
            topType.GetString() == "queue-operation")
        {
            if (root.TryGetProperty("content", out var qc) && qc.ValueKind == JsonValueKind.String)
            {
                var qs = qc.GetString();
                if (!string.IsNullOrEmpty(qs)) yield return qs;
            }
            yield break;
        }

        // Otherwise look for tool_result items in message.content[] (the arm).
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) yield break;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("type", out var t) || t.GetString() != "tool_result") continue;
            if (item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var s = c.GetString();
                if (!string.IsNullOrEmpty(s)) yield return s;
            }
        }
    }

    /// <summary>Returns the substring after <paramref name="prefix"/> up to <paramref name="stop"/>,
    /// or null if the prefix isn't present.</summary>
    private static string? ExtractAfter(string s, string prefix, char stop)
    {
        int i = s.IndexOf(prefix, StringComparison.Ordinal);
        if (i < 0) return null;
        i += prefix.Length;
        int j = s.IndexOf(stop, i);
        return j < 0 ? s.Substring(i) : s.Substring(i, j - i);
    }

    /// <summary>Returns the substring between <paramref name="open"/> and <paramref name="close"/>,
    /// or null if either marker is missing.</summary>
    private static string? ExtractBetween(string s, string open, string close)
    {
        int i = s.IndexOf(open, StringComparison.Ordinal);
        if (i < 0) return null;
        i += open.Length;
        int j = s.IndexOf(close, i, StringComparison.Ordinal);
        return j < 0 ? null : s.Substring(i, j - i);
    }

    /// <summary>
    /// Returns the last <paramref name="n"/> lines of a text file efficiently
    /// without loading the whole thing. Reads from EOF backwards in 8KB chunks
    /// until we have enough newlines.
    /// </summary>
    private static List<string> ReadLastLines(string path, int n)
    {
        const int bufSize = 8 * 1024;
        var result = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long pos = fs.Length;
        var carry = new List<byte>();
        var lines = new List<string>();

        while (pos > 0 && lines.Count < n + 1)
        {
            long readStart = Math.Max(0, pos - bufSize);
            int readLen = (int)(pos - readStart);
            var buf = new byte[readLen];
            fs.Seek(readStart, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, readLen);
            // Append the carry from the previous chunk so we don't split a line in half.
            var combined = new byte[readLen + carry.Count];
            Buffer.BlockCopy(buf, 0, combined, 0, readLen);
            for (int i = 0; i < carry.Count; i++) combined[readLen + i] = carry[i];
            // Split on \n; first piece may be partial-line carry for the next iteration.
            string s = System.Text.Encoding.UTF8.GetString(combined);
            var parts = s.Split('\n');
            // parts[0] could be partial — keep it as carry only if we're not at the file start.
            if (readStart > 0)
            {
                carry = new List<byte>(System.Text.Encoding.UTF8.GetBytes(parts[0]));
                for (int i = parts.Length - 1; i >= 1; i--) lines.Add(parts[i]);
            }
            else
            {
                for (int i = parts.Length - 1; i >= 0; i--) lines.Add(parts[i]);
            }
            pos = readStart;
        }

        // lines is now in reverse-chronological order (most recent first). Take up to n,
        // then reverse so the caller gets oldest→newest within the window.
        lines = lines.Where(s => !string.IsNullOrEmpty(s)).Take(n).ToList();
        lines.Reverse();
        return lines;
    }

    /// <summary>Trim and ?-check the assistant text. Returns true iff the message ends with `?`.</summary>
    public static bool EndsWithQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Allow trailing whitespace and trivial closing chars (quotes, parens) — questions
        // ending in `?")` should still count.
        var trimmed = text.TrimEnd(' ', '\t', '\r', '\n', '"', ')', ']', '*');
        if (trimmed.Length == 0) return false;
        return trimmed[^1] == '?';
    }

    /// <summary>Return at most <see cref="MaxTailLength"/> chars from the end of the text,
    /// breaking on the last whitespace before MaxTailLength to avoid mid-word cuts.</summary>
    public static string TailOf(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= MaxTailLength) return text;
        string tail = text.Substring(text.Length - MaxTailLength);
        int space = tail.IndexOf(' ');
        return space >= 0 && space < tail.Length - 1 ? tail.Substring(space + 1) : tail;
    }

    /// <summary>Return at most <paramref name="maxLength"/> chars from the start of the text,
    /// breaking on the last whitespace before maxLength to avoid mid-word cuts. Appends "...".</summary>
    public static string HeadOf(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        string head = text.Substring(0, maxLength);
        int space = head.LastIndexOf(' ');
        if (space > 0) head = head.Substring(0, space);
        return head + "...";
    }
}
