using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeHook;

/// <summary>
/// <c>ClaudeHook.exe --healthcheck</c>: runs at Windows login from the Run key.
/// Verifies the install is OK, shows a single Windows toast, and exits.
/// </summary>
internal static class HealthCheck
{
    // Events ClaudeHook subscribes to. StopFailure and UserPromptSubmit added 2026-05-20
    // — the StopFailure subscription is the fix for stuck-orange-desktop sessions.
    // PermissionRequest added 2026-05-20 — fires when inline y/n permission dialogs appear,
    // closing the gap where Bash/Edit/etc would block on user without firing Notification.
    private static readonly string[] AlertEvents =
    {
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse",
        "PermissionRequest",
        "Notification",
        "Stop", "StopFailure", "SessionEnd",
    };

    public static int Run()
    {
        var report = new StringBuilder();
        bool allGood = true;

        string exePath = Environment.ProcessPath ?? "";

        // Hooks wired in settings.json, running this exe directly.
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json");
        int wired = 0, missingEvents = 0;
        try
        {
            if (File.Exists(settingsPath))
            {
                var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
                var hooks = root?["hooks"] as JsonObject;
                foreach (string evt in AlertEvents)
                {
                    if (HasExeCommand(hooks?[evt] as JsonArray, exePath)) wired++;
                    else missingEvents++;
                }
            }
            else missingEvents = AlertEvents.Length;
        }
        catch (Exception ex)
        {
            allGood = false;
            report.AppendLine($"[fail] settings.json unreadable: {ex.Message}");
        }
        if (wired == AlertEvents.Length)
        {
            report.AppendLine($"[ok] all {AlertEvents.Length} hook events wired");
        }
        else
        {
            allGood = false;
            report.AppendLine($"[fail] hooks wired: {wired}/{AlertEvents.Length} — run --install");
        }

        // 3. DesktopNames pipe — informational. Not required: the toast fallback still works
        // without DesktopNames, just less prettily.
        bool desktopNamesUp = ProbePipe();
        report.AppendLine(desktopNamesUp
            ? "[ok] DesktopNames pipe reachable"
            : "[info] DesktopNames not running — falling back to Windows toasts");

        // Show the toast. One notify-icon + 4s timer + Application.Run pattern, same as
        // the hook fallback path.
        string title = allGood ? "ClaudeHook is ready" : "ClaudeHook needs attention";
        string body  = report.ToString().TrimEnd();
        string state = allGood ? "ready" : "asking";   // drives icon (info vs warning)

        Program.ShowBalloonExternal(state, title, body);

        return allGood ? 0 : 1;
    }

    private static bool HasExeCommand(JsonArray? evtArr, string exePath)
    {
        if (evtArr == null) return false;
        foreach (var group in evtArr)
        {
            if (group is not JsonObject g) continue;
            if (g["hooks"] is not JsonArray groupHooks) continue;
            foreach (var item in groupHooks)
            {
                if (item is JsonObject h &&
                    (h["command"]?.GetValue<string>() ?? "").Equals(exePath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static bool ProbePipe()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "DesktopNames",
                PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(150);
            return true;
        }
        catch { return false; }
    }
}
