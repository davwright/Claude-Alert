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
    /// Wider window for the monitor-liveness scan. A Monitor emits one task-notification
    /// per event (a build watch can tick dozens of times), so the "Monitor started" arm
    /// can be far behind the current tail. 400 lines covers a long-running watch without
    /// reading the whole (MB-scale) transcript.
    /// </summary>
    private const int MaxLinesForMonitorScan = 400;

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
    /// Scans the transcript tail for a Monitor watcher that is still live at this Stop.
    ///
    /// Why this exists: a Monitor tool call returns immediately ("Keep working"), so the
    /// turn ends and Stop fires — painting the desktop green — while the watcher runs on
    /// in the background. Monitors are NOT in the Stop payload's `background_tasks` array
    /// (that only tracks run_in_background Bash), so the transcript is the only signal.
    ///
    /// A monitor id is live if we saw its "Monitor started (task &lt;id&gt;" arm and its most
    /// recent task-notification event is not a completion (see <see cref="MonitorDoneTokens"/>).
    /// Both the arm and the per-event notifications land as tool_result items inside
    /// type:"user" lines; we match on the content string.
    ///
    /// Returns true if at least one armed monitor has no terminal event yet.
    /// </summary>
    public static bool HasLiveMonitor(string? transcriptPath)
    {
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath))
            return false;

        try
        {
            var tail = ReadLastLines(transcriptPath, MaxLinesForMonitorScan);

            // task-id → seen-a-terminal-event-since-arm. We walk oldest→newest so the
            // last write per id wins: arm sets false, a terminal event sets true, a
            // non-terminal event resets to false (a re-armed id, or just a fresh tick).
            var armed = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (string line in tail)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.Contains("Monitor started (task", StringComparison.Ordinal) &&
                    !line.Contains("task-notification", StringComparison.Ordinal))
                    continue;   // cheap reject before parsing

                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    foreach (string content in MonitorTexts(doc.RootElement))
                    {
                        // Arm: "Monitor started (task bc66xiqcd, timeout 70000ms)..."
                        // Only counts inside a tool_result (the genuine arm) — MonitorTexts
                        // yields those; assistant text quoting the phrase is not yielded.
                        string? armId = ExtractAfter(content, "Monitor started (task ", ',');
                        if (armId != null) { armed[armId] = false; continue; }

                        // Event: a queue-operation whose content is the <task-notification>.
                        // "...<task-id>bc66xiqcd</task-id>...<event>tick 5</event>..."
                        string? evtId = ExtractBetween(content, "<task-id>", "</task-id>");
                        if (evtId == null) continue;
                        string evtText = ExtractBetween(content, "<event>", "</event>") ?? "";
                        armed[evtId] = MonitorDoneTokens.Any(
                            t => evtText.Contains(t, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            // Live = armed and not yet terminal.
            return armed.Values.Any(terminal => !terminal);
        }
        catch { return false; }   // never let this break the Stop path
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
