using System.Text.Json;

namespace ClaudeHook;

// DevPulse's session heartbeat, formerly claude-heartbeat.sh. Folded in here because a
// bash hook took 1.5-12s to start and Claude Code cancelled it at its 3s timeout.
// Contract: %TEMP%/claude-heartbeats/<session>.json, read by DevPulse's HeartbeatWatcher,
// plus the legacy single %TEMP%/claude-heartbeat.json.
internal static partial class Program
{
    private static readonly string HeartbeatDir =
        Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "claude-heartbeats");

    private static void WriteHeartbeat(HookInput hook, int claudePid, int vscodePid)
    {
        // The events claude-heartbeat.sh was subscribed to; the others never reached DevPulse.
        string? status = hook.HookEventName switch
        {
            "PreToolUse" or "PostToolUse" or "UserPromptSubmit" => "thinking",
            "Notification" => "waiting",
            "Stop"         => "idle",
            _              => null,
        };
        if (status == null) return;

        using var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("status", status);
            w.WriteString("event", hook.HookEventName);
            w.WriteString("tool", hook.ToolName ?? "");
            w.WriteString("session", hook.SessionId ?? "");
            w.WriteString("cwd", hook.Cwd ?? "");
            w.WriteNumber("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            w.WriteNumber("pid", claudePid);
            w.WriteNumber("vscodePid", vscodePid);
            // Set by DevPulse's ClaudeManager when it spawns the session.
            w.WriteString("workItemId", Environment.GetEnvironmentVariable("DEVPULSE_WORK_ITEM_ID") ?? "");
            w.WriteString("org", Environment.GetEnvironmentVariable("DEVPULSE_ORG") ?? "");
            w.WriteString("project", Environment.GetEnvironmentVariable("DEVPULSE_PROJECT") ?? "");
            w.WriteString("provider", Environment.GetEnvironmentVariable("DEVPULSE_PROVIDER") ?? "");
            w.WriteEndObject();
        }
        buf.WriteByte((byte)'\n');
        byte[] bytes = buf.ToArray();

        Directory.CreateDirectory(HeartbeatDir);
        string file = Path.Combine(HeartbeatDir, $"{(string.IsNullOrEmpty(hook.SessionId) ? "unknown" : hook.SessionId)}.json");
        // Write-then-rename so the watcher never reads a half-written file.
        string tmp = $"{file}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, file, overwrite: true);
        File.WriteAllBytes(Path.Combine(HeartbeatDir, "..", "claude-heartbeat.json"), bytes);
    }
}
