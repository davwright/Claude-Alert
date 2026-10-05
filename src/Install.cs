using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ClaudeHook;

/// <summary>
/// <c>ClaudeHook.exe --install</c>: standalone install path.
///   1. Wires the Claude Code hooks into ~/.claude/settings.json as exec-form entries
///      that run this exe directly, and deletes the old claude-alert.sh wrapper.
///   3. Registers an HKCU\...\Run autostart entry that runs --healthcheck.
/// </summary>
internal static class Install
{
    private const string AutostartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutostartValueName = "ClaudeHook";

    public static int Run() => RunInternal(showToast: false);

    /// <summary>
    /// Same as Run() but also pops a Windows toast at the end so the user sees the
    /// result without needing a terminal. Used when the user double-clicks the exe
    /// (the typical interactive entry point) or runs --install explicitly.
    /// </summary>
    public static int RunWithToast() => RunInternal(showToast: true);

    private static int RunInternal(bool showToast)
    {
        string exePath  = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath null");
        string exeDir   = Path.GetDirectoryName(exePath)!;
        string shPath   = Path.Combine(exeDir, "claude-alert.sh");
        string shBash   = shPath.Replace('\\', '/');

        var report = new System.Text.StringBuilder();
        bool allGood = true;
        int addedHooks = 0;

        void Step(bool ok, string okMsg, string failMsg)
        {
            if (ok) report.AppendLine($"[ok] {okMsg}");
            else    { report.AppendLine($"[fail] {failMsg}"); allGood = false; }
        }

        Console.WriteLine($"ClaudeHook install");
        Console.WriteLine($"  exe : {exePath}");

        try { addedHooks = WireHooks(exePath, shBash); Step(true, $"{Subscriptions.Length} hook subscriptions wired", ""); }
        catch (Exception ex) { Step(false, "", $"wiring hooks: {ex.Message}"); }

        try { File.Delete(shPath); Step(true, "claude-alert.sh removed", ""); }
        catch (Exception ex) { Step(false, "", $"removing claude-alert.sh: {ex.Message}"); }

        try { RegisterAutostart(exePath); Step(true, "autostart registered", ""); }
        catch (Exception ex) { Step(false, "", $"autostart: {ex.Message}"); }

        Console.Write(report.ToString());
        if (allGood) Console.WriteLine("Done. Restart Claude Code to pick up the new hooks.");

        if (showToast)
        {
            string title = allGood ? "ClaudeHook installed" : "ClaudeHook install — issues";
            string body  = (allGood
                ? "Hooks wired into Claude Code · autostart enabled · ready.\n"
                : "Some steps failed — see details below.\n") + report.ToString().TrimEnd();
            string state = allGood ? "ready" : "asking";
            Program.ShowBalloonExternal(state, title, body);
        }

        return allGood ? 0 : 1;
    }

    // ---- settings.json hook wiring -----------------------------------------

    /// <summary>One entry we want present in settings.json.</summary>
    private sealed record HookEntry(string Event, string Matcher, string? ExtraArg, int TimeoutSec);

    /// <summary>
    /// The full set of subscriptions ClaudeHook installs. Rationale per row:
    ///   - SessionStart / UserPromptSubmit / PreToolUse → "busy" state (UserPromptSubmit
    ///     is the earliest signal we can get that the user has engaged Claude).
    ///   - Notification (3 separate matchers): different UX per subtype. auth_success
    ///     is subscribed but suppressed at runtime — easier to filter in the exe than
    ///     to gamble that Claude won't add new subtypes we'd miss.
    ///   - Stop AND StopFailure → "ready". This is the f75a006c-stuck-orange fix: Stop
    ///     ONLY fires on a successful turn end, StopFailure fires on rate limits / auth
    ///     errors / billing problems. Subscribing to only Stop = stuck busy forever.
    ///   - SessionEnd → "idle". Final clearance.
    /// </summary>
    private static readonly HookEntry[] Subscriptions =
    {
        new("SessionStart",      "*",                 null, 5),
        new("UserPromptSubmit",  "*",                 null, 5),
        new("PreToolUse",        "*",                 null, 5),
        new("PostToolUse",       "*",                 null, 5),   // DN's "the permission prompt was answered" signal
        new("PermissionRequest", "*",                 null, 5),   // fires when inline y/n dialog appears
        new("Notification",      "permission_prompt", "--notification-kind permission_prompt", 10),
        new("Notification",      "idle_prompt",       "--notification-kind idle_prompt",       10),
        new("Notification",      "auth_success",      "--notification-kind auth_success",       5),
        new("Stop",              "*",                 null, 10),
        new("StopFailure",       "*",                 null, 10),
        new("SessionEnd",        "*",                 null, 5),
    };

    private static int WireHooks(string exePath, string legacyShBash)
    {
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json");

        JsonNode root;
        if (File.Exists(settingsPath))
        {
            string raw = File.ReadAllText(settingsPath);
            root = JsonNode.Parse(raw, new JsonNodeOptions { PropertyNameCaseInsensitive = false })
                   ?? new JsonObject();
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            root = new JsonObject();
        }

        if (root is not JsonObject obj)
            throw new InvalidOperationException("settings.json root is not an object");

        if (obj["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            obj["hooks"] = hooks;
        }

        // Sweep all our event keys and remove any existing claude-alert.sh entries
        // before we re-add. This migrates users from old subscription shapes (the
        // pre-StopFailure layout had no UserPromptSubmit and a single Notification
        // matcher=*; that would now collide with the new per-subtype entries).
        var allEventNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sub in Subscriptions) allEventNames.Add(sub.Event);
        foreach (var evt in allEventNames)
        {
            if (hooks[evt] is not JsonArray arr) continue;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is not JsonObject group) continue;
                if (group["hooks"] is not JsonArray groupHooks) continue;
                for (int j = groupHooks.Count - 1; j >= 0; j--)
                {
                    if (groupHooks[j] is JsonObject h && IsOurHook(h, exePath, legacyShBash))
                        groupHooks.RemoveAt(j);
                }
                // Drop the group entirely if we emptied it.
                if (groupHooks.Count == 0) arr.RemoveAt(i);
            }
            if (arr.Count == 0) hooks.Remove(evt);
        }

        // Add the new subscriptions.
        int added = 0;
        foreach (var sub in Subscriptions)
        {
            if (hooks[sub.Event] is not JsonArray arr)
            {
                arr = new JsonArray();
                hooks[sub.Event] = arr;
            }

            // Find or create the matcher group.
            JsonObject? group = null;
            foreach (var item in arr)
            {
                if (item is JsonObject g && (g["matcher"]?.GetValue<string>() ?? "") == sub.Matcher)
                {
                    group = g;
                    break;
                }
            }
            if (group == null)
            {
                group = new JsonObject { ["matcher"] = sub.Matcher, ["hooks"] = new JsonArray() };
                arr.Add(group);
            }
            if (group["hooks"] is not JsonArray groupHooks)
            {
                groupHooks = new JsonArray();
                group["hooks"] = groupHooks;
            }

            // Exec form (args present): Claude Code spawns the exe itself. The old bash
            // wrapper took 1.5-12s just to start under load, so the hook runner cancelled it
            // at its timeout and DN never heard of the event. async replaces the wrapper's
            // background-and-disown: Claude Code doesn't wait on us.
            var argv = new JsonArray();
            if (sub.ExtraArg != null) foreach (var a in sub.ExtraArg.Split(' ')) argv.Add(a);
            groupHooks.Add(new JsonObject
            {
                ["type"]    = "command",
                ["command"] = exePath,
                ["args"]    = argv,
                ["async"]   = true,
                ["timeout"] = sub.TimeoutSec,
            });
            added++;
        }

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(settingsPath, root.ToJsonString(opts));
        return added;
    }

    /// <summary>True iff this hook entry was installed by us: the exe itself, or the old bash wrapper.</summary>
    private static bool IsOurHook(JsonObject h, string exePath, string legacyShBash)
    {
        string cmd = h["command"]?.GetValue<string>() ?? "";
        return cmd.Equals(exePath, StringComparison.OrdinalIgnoreCase)
            || cmd.StartsWith(legacyShBash, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Windows autostart -------------------------------------------------

    private static void RegisterAutostart(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AutostartKey);
        if (key == null) throw new InvalidOperationException("Could not open HKCU\\...\\Run");
        // Quote the path in case it contains spaces. Healthcheck mode is silent unless
        // there's a problem to surface.
        key.SetValue(AutostartValueName, $"\"{exePath}\" --healthcheck", RegistryValueKind.String);
    }
}
