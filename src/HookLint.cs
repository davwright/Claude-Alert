using System.Text.Json.Nodes;

namespace ClaudeHook;

/// <summary>
/// Finds shell-form hooks: a "command" hook without "args" runs through Git Bash, which costs
/// 1.5s idle and 5-12s under load here. Claude Code cancels a hook at its timeout without a word,
/// so an alert drops its event and a guard fails open (2000+ cancelled runs on 2026-10-05).
/// Checked at every SessionStart (toast) and by <c>--lint-hooks</c> (exit 1).
/// </summary>
internal static class HookLint
{
    public static List<string> Find(string? cwd)
    {
        string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var found = new List<string>();

        var sources = new List<(string Label, string Path)>
        {
            ("user settings", Path.Combine(home, "settings.json")),
            ("user settings.local", Path.Combine(home, "settings.local.json")),
        };
        if (!string.IsNullOrEmpty(cwd))
        {
            sources.Add(("project settings", Path.Combine(cwd, ".claude", "settings.json")));
            sources.Add(("project settings.local", Path.Combine(cwd, ".claude", "settings.local.json")));
        }
        sources.AddRange(EnabledPluginHookFiles(home));

        foreach (var (label, path) in sources)
        {
            if (!File.Exists(path)) continue;
            if (JsonNode.Parse(File.ReadAllText(path))?["hooks"] is not JsonObject hooks) continue;
            foreach (var (evt, groups) in hooks)
            {
                if (groups is not JsonArray arr) continue;
                foreach (var g in arr)
                {
                    if (g?["hooks"] is not JsonArray hs) continue;
                    foreach (var h in hs)
                    {
                        if (h is not JsonObject o) continue;
                        string type = o["type"]?.GetValue<string>() ?? "command";
                        if (type != "command" || o["args"] != null) continue;
                        string cmd = o["command"]?.GetValue<string>() ?? "";
                        found.Add($"{label} {evt}: {(cmd.Length > 90 ? cmd[..90] + "..." : cmd)}");
                    }
                }
            }
        }
        return found;
    }

    /// <summary>hooks/hooks.json of every plugin enabled in user settings, at its installed version.</summary>
    private static IEnumerable<(string, string)> EnabledPluginHookFiles(string home)
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "settings.json")));
        var installed = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "plugins", "installed_plugins.json")))?["plugins"];
        if (settings?["enabledPlugins"] is not JsonObject enabled) yield break;
        foreach (var (name, on) in enabled)
        {
            if (on?.GetValue<bool>() != true) continue;
            if (installed?[name] is not JsonArray installs) continue;
            foreach (var i in installs)
            {
                string? dir = i?["installPath"]?.GetValue<string>();
                if (dir != null) yield return ($"plugin {name}", Path.Combine(dir, "hooks", "hooks.json"));
            }
        }
    }

    public static string Summary(List<string> found) =>
        $"{found.Count} hook(s) run through Git Bash (slow; cancelled at timeout, guards fail open). " +
        "Use exec form: \"command\" = node or an .exe, plus \"args\": [...].\n" + string.Join("\n", found);
}
