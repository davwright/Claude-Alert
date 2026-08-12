using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHook;

// Extends Program with parent-walk helpers (partial class — see Program.cs).
internal static partial class Program
{
    // ---- public API ---------------------------------------------------------

    /// <summary>
    /// Result of walking the process tree from a known starting PID.
    /// We report everything to DesktopNames and let it decide what to use.
    /// </summary>
    internal sealed class WalkResult
    {
        public List<ChainEntry> Chain { get; init; } = new();
        public string Outcome { get; init; } = "";   // see WalkOutcomes
        public int VsCodePid { get; init; }          // first Code/Cursor in chain, or 0
    }

    /// <summary>Stable outcome labels we send to DesktopNames.</summary>
    private static class WalkOutcomes
    {
        public const string FoundCode      = "found-code";       // hit Code/Cursor/Code - Insiders
        public const string NoCodeAncestor = "no-code-ancestor"; // walked to top, no match
        public const string ChainBroken    = "chain-broken";     // a hop returned empty name
        public const string PpidWasOne     = "ppid-was-1";       // Git Bash MSYS quirk
        public const string Skipped        = "skipped";          // no fromPid was passed at all
    }

    /// <summary>
    /// Walk parents from <paramref name="fromPid"/> up to 8 hops, looking for
    /// Code.exe / Cursor.exe / Code - Insiders.exe. Returns the full chain so
    /// DesktopNames can match by any pid/name combination it wants.
    ///
    /// Per-session positive-result cache: once we've found a VSCode pid for a
    /// session, we skip the walk on subsequent hooks unless the pid has died.
    /// </summary>
    internal static WalkResult WalkChain(string? sessionId, int fromPid)
    {
        // Cache check (session-scoped, positive only).
        string? cachePath = string.IsNullOrEmpty(sessionId) ? null
            : Path.Combine(CacheDir, $"vscode-pid-{sessionId}.json");
        if (cachePath != null && File.Exists(cachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<WalkCacheEntry>(File.ReadAllText(cachePath));
                if (cached != null && cached.VsCodePid > 4 && IsAlive(cached.VsCodePid))
                {
                    Log($"  walk cache hit: vscodePid={cached.VsCodePid}, chain has {cached.Chain.Count} entries");
                    return new WalkResult
                    {
                        Chain = cached.Chain,
                        Outcome = WalkOutcomes.FoundCode,
                        VsCodePid = cached.VsCodePid,
                    };
                }
                if (cached != null && cached.VsCodePid > 4) Log($"  walk cache stale (pid {cached.VsCodePid} gone)");
            }
            catch (Exception ex) { Log($"  walk cache read failed: {ex.Message}"); }
        }

        // No start point → nothing to do. DN will fall back to cwd resolution.
        // Pick a starting PID. We prefer the fromPid bash gave us (claude.exe — stable
        // for the session), but fall back to walking from our own PID when bash handed
        // us 0 or 1. The 1 case is Git Bash's MSYS quirk: $PPID is sometimes the init
        // sentinel rather than the real parent. Our own chain starts at:
        //   ClaudeHook.exe → subshell-bash → original-bash → claude.exe → Code → ...
        // The original bash has usually exited by the time we walk (we were backgrounded
        // by it), so some early hops break — but the upper layers (claude, Code) usually
        // survive, so we still report a useful partial chain instead of giving up.
        int startPid;
        string startReason;
        if (fromPid > 1)               { startPid = fromPid;                 startReason = $"fromPid={fromPid}"; }
        else if (fromPid == 1)         { startPid = Environment.ProcessId;   startReason = "ppid=1 fallback → self"; }
        else                            { startPid = Environment.ProcessId;   startReason = "no fromPid → self"; }
        Log($"  walk start: {startReason}");

        // Walk. We don't stop at the first Code/Cursor we see — there can be multiple
        // (worker → window) and we want the topmost (the window). Track the most recent
        // match; final outcome reflects whether we ever saw one.
        //
        // Broken hops (a parent that already exited) don't abort the walk: we skip past
        // them and try the next parent. The chain might have a gap but the upper Code/
        // claude entries are what DesktopNames actually needs.
        var sw = Stopwatch.StartNew();
        var chain = new List<ChainEntry>();
        int vscodePid = 0;
        int pid = startPid;
        bool sawBroken = false;

        for (int hop = 0; hop < 10; hop++)
        {
            string name = GetProcessName(pid);
            if (string.IsNullOrEmpty(name))
            {
                sawBroken = true;
                int next = GetParentPid(pid);
                if (next <= 4) break;
                pid = next;
                continue;
            }
            chain.Add(new ChainEntry { Pid = pid, Name = name });

            string norm = name.ToLowerInvariant();
            if (norm == "code" || norm == "cursor" || norm == "code - insiders")
                vscodePid = pid;

            int parent = GetParentPid(pid);
            if (parent <= 4) break;   // System / Idle / Secure System / out of tree
            pid = parent;
        }

        // Outcome priority: code-found beats everything; broken-chain beats the generic
        // no-code-ancestor; ppid-was-1 is reported separately so DN can see when we had
        // to fall back to walking from our own PID.
        string outcome;
        if (vscodePid > 0)         outcome = WalkOutcomes.FoundCode;
        else if (fromPid == 1)     outcome = WalkOutcomes.PpidWasOne;
        else if (sawBroken)        outcome = WalkOutcomes.ChainBroken;
        else                       outcome = WalkOutcomes.NoCodeAncestor;
        Log($"  walk from {startPid}: {chain.Count} hops, outcome={outcome}, vscodePid={vscodePid}, {sw.ElapsedMilliseconds}ms");

        // Cache only positive results (we found Code/Cursor).
        if (vscodePid > 0 && cachePath != null)
        {
            try
            {
                File.WriteAllText(cachePath, JsonSerializer.Serialize(new WalkCacheEntry
                {
                    VsCodePid = vscodePid,
                    Chain = chain,
                }));
            }
            catch (Exception ex) { Log($"  cache write failed: {ex.Message}"); }
        }

        return new WalkResult { Chain = chain, Outcome = outcome, VsCodePid = vscodePid };
    }

    private sealed class WalkCacheEntry
    {
        [JsonPropertyName("vscodePid")] public int VsCodePid { get; set; }
        [JsonPropertyName("chain")]     public List<ChainEntry> Chain { get; set; } = new();
    }

    // ---- helpers ------------------------------------------------------------

    private static string GetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;   // no ".exe" suffix
        }
        catch { return ""; }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    // --- Native parent-PID lookup via NtQueryInformationProcess --------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public UIntPtr UniqueProcessId;
        public UIntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr ProcessHandle,
        int ProcessInformationClass,
        ref PROCESS_BASIC_INFORMATION ProcessInformation,
        int ProcessInformationLength,
        out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static int GetParentPid(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(h, 0 /* ProcessBasicInformation */,
                ref info, Marshal.SizeOf(info), out _);
            if (status != 0) return 0;
            return (int)info.InheritedFromUniqueProcessId.ToUInt32();
        }
        finally { CloseHandle(h); }
    }
}
