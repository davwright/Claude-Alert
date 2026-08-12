# DesktopNames ↔ Claude-Alert integration

## ⚠️ Migration instructions for DesktopNames (2026-05-20 wire schema)

**Breaking change.** ClaudeHook no longer sends `state`. DesktopNames must switch to keying on `hookEvent` instead. Until that switch, sessions arriving from a new ClaudeHook build will look like they have no state and won't render.

### Why the change

`state` was a 4-value enum (`busy`/`asking`/`ready`/`idle`) that collapsed seven different Claude hook events into four buckets. This forced DesktopNames to guess the original event from heuristics, and conflated meaningfully different situations — e.g. "Claude finished a turn" (clean Stop) was lumped with "Claude went idle waiting for you to come back" (Notification/idle_prompt). The user has explicit colour semantics (below) that need 1-to-1 event visibility to render correctly.

ClaudeHook now sends `hookEvent` (and, for Notification events, `notificationKind`) **raw and unconflated**. DesktopNames decides what colour each event produces.

### Colour mapping the user wants

| Colour | Meaning to the user | Trigger events |
|--------|---------------------|----------------|
| 🟢 **green** | Claude is **dormant or finished** — nothing for me to do. | `hookEvent: "Stop"` with `lastMessageEndsWithQuestion: false` (or null), `hookEvent: "SessionEnd"` |
| 🟡 **yellow** | Claude is **waiting on me** (I'm blocking him). Stays yellow indefinitely — could be hours if user went to lunch. | • `hookEvent: "PermissionRequest"` (inline y/n dialog for sensitive tools)<br>• `hookEvent: "Notification"` with `notificationKind: "permission_prompt"` or `"idle_prompt"`<br>• `hookEvent: "PreToolUse"` with `toolName` in `{"AskUserQuestion", "ExitPlanMode"}` (Claude is invoking a tool whose entire purpose is to block on user input)<br>• `hookEvent: "Stop"` with **`lastMessageEndsWithQuestion: true`** (Claude finished its turn with a question — see `lastMessageTail` for the text) |
| 🟠 **orange** | Claude is **working** — leave him alone. | `hookEvent: "SessionStart"`, `"UserPromptSubmit"`, `"PreToolUse"` (except the blocking-tool cases above) |
| 🔴 **red** *(suggested)* | Claude **stopped with an error** — rate-limit, auth failure, billing issue. Same "finished" semantic as green but worth a more attention-getting colour because the user may need to act. The `errorType` field carries the kind. | `hookEvent: "StopFailure"` |
| (no colour / clear indicator) | Session ended cleanly. | (also `SessionEnd`, same as green — DN's choice) |

> **Critical distinction the user cares about:** *finished* (green/red, Claude is dormant) is NOT the same as *waiting on user* (yellow, Claude is blocked). Yellow must stay yellow regardless of how long it's been showing — that's the whole point.

### Resolving the "stuck orange while Claude is asking" problem (closed 2026-05-20)

The longstanding gap — Claude asks an inline `y/n` for a sensitive Bash/Edit operation, no `Notification` fires, desktop stays orange — has three layers of coverage now, all on the wire as raw events:

1. **`PermissionRequest`** — fires when the dialog actually appears. Subscribed by ClaudeHook, sent verbatim. **This is the primary signal for inline-y/n.** Has a `tool name` matcher field if DN wants to differentiate which tool triggered.
2. **`PreToolUse` with `toolName: "AskUserQuestion"`** — Claude is using its structured-question tool. Confirmed in the user's log firing 6 times today, always blocking.
3. **`PreToolUse` with `toolName: "ExitPlanMode"`** — Claude has finished planning and is asking for approval before executing.

ClaudeHook does **not** re-tag any of these. It sends them with their native `hookEvent` and `toolName`. DesktopNames keys on the combination and chooses yellow. If Anthropic adds new blocking tools later, DN can grow its list without a ClaudeHook change.

### Stop-with-question detection: how it works and how it fails

For the Stop+question case, ClaudeHook reads the tail of the transcript JSONL file (`transcriptPath` in the payload) using a small, fixed parser. The expected schema is:

```jsonc
{ "type": "assistant",
  "message": {
    "role": "assistant",
    "content": [
      { "type": "text", "text": "..." },
      { "type": "tool_use", "name": "...", ... }
    ]
  }, ... }
```

ClaudeHook scans the last ~30 lines, finds the most recent assistant entry with a `text` item in `content`, and applies a `?`-tail check (trimming trailing whitespace, quotes, parens, asterisks).

**Cost:** ~1–5ms per Stop (file's append-only so the OS page cache helps). Fires once per turn end.

**Heuristic precision:** roughly 80% in observation — "Should I proceed?" hits, "Make sense?" also hits (technically a question, soft check-in). DN may want to apply its own filtering on the `lastMessageTail` field.

**Schema-break behaviour:**

- If we can't find the expected `type:"assistant" → message.content[*].type:"text"` shape *at all* in the tail, ClaudeHook treats it as a schema break.
- First time per `sessionId`: writes `SCHEMA_CHANGED ...` to `claudehook.log` AND pops a Windows toast ("ClaudeHook needs update — Claude's transcript format changed").
- Subsequent times for the same session: writes `SCHEMA_CHANGED ... (already warned for this session)` and stays silent.
- In either case the payload's `lastMessageEndsWithQuestion` and `lastMessageTail` are `null`, so DN falls back to colouring `Stop → green`. **The bug fails closed**, never silently green-when-actually-asking with stale data.

**For DesktopNames implementers:** treat `lastMessageEndsWithQuestion: null` exactly like `false` (i.e. green). Only `true` should trigger the yellow branch.

### Background-work-at-Stop detection (Monitor + run_in_background)

**The problem.** A `Monitor` tool call returns immediately ("Keep working — do not poll"), so the turn ends and `Stop` fires — painting the desktop green — while the watcher runs on in the background for minutes. Monitor wake-ups (task-notifications) fire **no hooks at all** (verified: they arrive as `queue-operation` transcript lines, not `UserPromptSubmit`), so there's no later event to correct the green. The desktop stays green for the entire watch. Same story for `run_in_background` Bash that outlives the turn.

Anthropic was asked to add a `BackgroundTasksIdle` hook event for exactly this and [closed it as not-planned (#45781)](https://github.com/anthropics/claude-code/issues/45781). So there is no clean event; ClaudeHook detects it at `Stop` from two sources and sets `backgroundActive`:

| Source | Covers | Reliability |
|---|---|---|
| Stop payload's `background_tasks` array (non-empty) | `run_in_background` Bash shells | **Deterministic** — Claude Code populates it |
| Transcript scan for a live Monitor (`TranscriptPeek.HasLiveMonitor`) | **Monitor** watchers | Heuristic — `background_tasks` does **not** list monitors (verified empirically) |

**How the Monitor scan works.** On `Stop`, ClaudeHook scans the last 400 transcript lines. A monitor is *armed* by a `tool_result` reading `Monitor started (task <id>`; each event lands as a `queue-operation` line whose `content` holds `<task-id>…</task-id>` + `<event>…</event>`. A monitor id is **live** if its most recent event isn't a completion. Completion tokens: `[Monitor timed out` (harness-generated, reliable) plus author-chosen sentinels (`completed`, `succeeded`, `-done`, `settled`, `---final---`, `failed`, `fatal`, `timed out`, `closure complete`).

**Known limitation — errs toward busy, never toward false-green.** If a monitor's script exits on a success word we don't recognise (and without the harness timeout line), or its terminal event scrolled out of the 400-line window, the Stop stays `backgroundActive` (orange) a little too long. This is the deliberate direction: better a stale orange than a green that says "done" while a build is still running. It self-heals — the user chose **"clear on next real hook"**: the next genuine `PreToolUse`/`UserPromptSubmit` repaints orange anyway (real work), and once the monitor's transcript lines age past the scan window a later `Stop` reads green. Monitors do **not** show a distinct colour; they share orange with ordinary "Claude is working" — a `Monitor` is Claude working, not Claude blocked on the user (that's yellow).

### The mapping table, in code

```csharp
// Tools whose entire purpose is to block on user input. Treat PreToolUse for these
// as yellow even though it's technically a "tool is starting" event.
private static readonly HashSet<string> BlockingTools = new(StringComparer.Ordinal)
{
    "AskUserQuestion",
    "ExitPlanMode",
    // Add others here as they're identified.
};

string ColourFor(StatePayload p) => p.HookEvent switch
{
    // Inline y/n dialog — Claude is blocked on user until they answer.
    "PermissionRequest" => "yellow",

    "Notification" => p.NotificationKind switch
    {
        "permission_prompt" => "yellow",          // user must approve a tool
        "idle_prompt"       => "yellow",          // claude prompting because idle
        "auth_success"      => null,              // never arrives — claude-alert suppresses it
        _                   => "yellow",          // unknown subtype, default to user-attention
    },

    // PreToolUse splits: blocking tools → yellow, everything else → orange.
    "PreToolUse"
        when p.ToolName != null && BlockingTools.Contains(p.ToolName) => "yellow",
    "PreToolUse"            => "orange",

    "SessionStart"          => "orange",          // working
    "UserPromptSubmit"      => "orange",          // user just engaged, claude about to think

    // Stop splits, in priority order:
    //   1. background work still running (live Monitor watcher or run_in_background Bash)
    //      → orange. The turn ended but Claude is still watching something — not "done".
    //   2. ends-with-question → yellow (claude is asking via chat).
    //   3. otherwise green (clean finish).
    // All three fields are populated by ClaudeHook on Stop (transcript-peek + payload's
    // background_tasks). backgroundActive defaults false if the peek failed.
    "Stop" when p.BackgroundActive              => "orange",
    "Stop" when p.LastMessageEndsWithQuestion == true => "yellow",
    "Stop"                  => "green",

    "StopFailure"           => "red",             // turn done with error (errorType has detail)
    "SessionEnd"            => null,              // clear the indicator
    _                       => null,              // unknown event
};
```

### What DesktopNames needs to change

1. **Remove the `state` field lookup.** It's gone. Any switch on `state` will fall through.
2. **Switch on `hookEvent`** for the primary classification. Use the table above (or your own colour choices — those are the user's preferences but you own the rendering).
3. **For `Notification` events specifically, also check `notificationKind`.** It's `permission_prompt`, `idle_prompt`, or `null` (if Claude's matcher value is null for that subtype — shouldn't happen but be defensive).
4. **For `StopFailure`, read `errorType`** if you want to differentiate red intensity or tooltip text by error kind (`rate_limit`, `authentication_failed`, etc.).
5. **Multi-Claude priority on a single desktop:** suggested order, highest wins → `yellow > orange > red > green`. Rationale: yellow demands immediate attention; orange means active work; red means an error happened but Claude isn't blocking; green is the resting state. (The previous priority was `asking > busy > ready > idle` — same ordering, just renamed.)

### Migration test

After updating DesktopNames, send a synthetic payload from PowerShell to verify each colour:

```powershell
function Send-Test($json) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DesktopNames', 'InOut')
    $pipe.Connect(500)
    $w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true
    $r = New-Object System.IO.StreamReader($pipe)
    $w.WriteLine($json); $r.ReadLine()
    $pipe.Dispose()
}

# orange
Send-Test '{"type":"state","source":"claude-code","title":"Test","body":"Working","sessionId":"test-orng","cwd":"c:/git/tools/Claude-Alert","hookEvent":"PreToolUse","toolName":"Edit"}'

# yellow
Send-Test '{"type":"state","source":"claude-code","title":"Test","body":"Allow Bash?","sessionId":"test-ylw","cwd":"c:/git/tools/Claude-Alert","hookEvent":"Notification","notificationKind":"permission_prompt","message":"Allow Bash command?"}'

# green
Send-Test '{"type":"state","source":"claude-code","title":"Test","body":"Ready","sessionId":"test-grn","cwd":"c:/git/tools/Claude-Alert","hookEvent":"Stop"}'

# red
Send-Test '{"type":"state","source":"claude-code","title":"Test","body":"Stopped: rate_limit","sessionId":"test-red","cwd":"c:/git/tools/Claude-Alert","hookEvent":"StopFailure","errorType":"rate_limit"}'

# clear
Send-Test '{"type":"state","source":"claude-code","title":"Test","body":"","sessionId":"test-clr","cwd":"c:/git/tools/Claude-Alert","hookEvent":"SessionEnd"}'
```

Each should produce the right colour on whichever desktop the cwd resolves to.

### Backward compatibility

There is none, deliberately. The previous `state` field is gone from the wire — DN will see `state: undefined` until it ships the migration. ClaudeHook's `--install` is idempotent and migration-safe (sweeps old subscriptions before writing new ones), but the **wire schema break is intentional and unprefixed** to force the cutover. The user explicitly chose this over a transitional both-fields shape.

---

## Fix list for ClaudeHook (prioritized by real data, added 2026-05-20)

DesktopNames-side resolver is now in good shape after the 2026-05-20 work (workspace-folder index, sticky bindings, learned paths). The remaining "stuck busy" symptom is a **hook-subscription gap**, not a resolver issue. See full hook reference: the skill at `~/.claude/skills/claude-code-hooks/SKILL.md` (Anthropic docs https://code.claude.com/docs/en/hooks).

### Fix #A — subscribe to `StopFailure` (HIGH IMPACT, root cause of stuck-busy) — ✅ DONE 2026-05-20

**Symptom observed today:** sessions `f75a006c` and `02f6d203` fire ~20 `PreToolUse` events each and **zero `Stop` events**. DN paints them busy forever (well, until 15min stale-busy sweep). Other sessions (`8eb30580`, `288078f7`, `d2586b00`, `355bae2d`) fire `Stop` normally.

**Cause (per docs):** `Stop` fires only on **successful** turn completion. When the turn ends due to rate-limit, auth error, billing error, network failure, or invalid request, Claude Code fires **`StopFailure` instead**, and `Stop` is skipped entirely. The Notification subtypes `permission_prompt` and `idle_prompt` also surface as `Notification` and need their own handling.

**What to add** to the install path (`ClaudeHook.exe --install` writing to `~/.claude/settings.json`):

```jsonc
{
  "hooks": {
    "StopFailure": [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<path>/claude-alert.sh", "timeout": 5 }] }]
  }
}
```

**Mapping:** treat `StopFailure` as `state=ready` (same as `Stop`) — the turn is done from the user's perspective; they need to act. **Pass the error type into the message:**

- The `StopFailure` matcher value is the error type (`rate_limit`, `authentication_failed`, `billing_error`, `network_error`, etc.).
- Put it into `body` so DN's tooltip can surface it: e.g. `"Ready (after rate_limit)"`.

This single change closes the stuck-busy symptom on every session that's ever errored.

> **Shipped (2026-05-20):** `StopFailure` is subscribed and sent to DN with `hookEvent: "StopFailure"` and a new `errorType` field (`hook.error_type` / `hook.error` / `hook.reason` — first present wins, robust to whichever field Claude ends up using). The fallback-toast body becomes `"Stopped: <errorType>"`. (Note: the `state` field that originally drove this is no longer on the wire — see the migration section at the top of this doc.)

### Fix #B — pass `Notification` matcher subtype into the message (MEDIUM) — ✅ DONE 2026-05-20

`Notification` fires for several different things and the matcher value tells you which:

| Matcher | Meaning |
|---|---|
| `permission_prompt` | Claude is asking the user to approve a tool use |
| `idle_prompt` | Claude has been idle waiting for input |
| `auth_success` | Auth flow completed (not really an "asking" — could be skipped or sent as info) |

The current OUT JSON has a `message` field that's already populated with the question text. Add a `notificationType` (or repurpose `hookSource` which is already in the wire format) carrying the matcher value, so DN can:

- Skip `auth_success` (not user-attention-needed).
- Show different tooltip text per type.
- Optionally use a different breathe rhythm for `idle_prompt` vs `permission_prompt`.

> **Shipped (2026-05-20):** Implementation took a slightly different shape than this section suggests because the **hook payload itself doesn't carry the matcher subtype** — Claude only routes via the matcher field, it doesn't tell the receiver which matcher matched. So ClaudeHook installs **three separate hook entries** in `~/.claude/settings.json`, one per Notification matcher, each invoking `claude-alert.sh --notification-kind <kind>`. The bash wrapper forwards extras via `"$@"` to the exe. The exe reads `--notification-kind` and sets the new `notificationKind` field on the outbound payload. `auth_success` is subscribed but **suppressed before pipe send** — ClaudeHook returns early with `EXIT notification=auth_success (suppressed)`, so DN never sees it.

### Fix #C — `UserPromptSubmit` as an explicit "user engaging" event (LOW, nice-to-have) — ✅ DONE 2026-05-20

`UserPromptSubmit` fires **before** Claude processes a user prompt — earlier than the first `PreToolUse`. Sending it as `state=busy` gives DN a sharper transition from "ready" (Claude waiting) → "busy" (user just sent something, Claude about to think). Today we infer this from the first `PreToolUse`, but Claude can take several seconds before the first tool call, during which DN stays green.

Subscribe with `"matcher": ""` (UserPromptSubmit ignores matchers anyway) and map to `state=busy`. The 1Hz `busy` debounce on the client already handles spam.

> **Shipped (2026-05-20):** subscription added with `"matcher": "*"` (UserPromptSubmit ignores it either way; `*` keeps the JSON visually consistent with the other entries). Sent to DN as `hookEvent: "UserPromptSubmit"` — DN colour-maps it to orange. (Previously this was internally mapped to `state:busy`; that internal mapping is now only used for ClaudeHook's busy-debounce, not on the wire.)

### Fix #D — diagnostic: log when `Stop` and `StopFailure` would otherwise be missed (LOW) — ✅ DONE 2026-05-20

If a session fires `PreToolUse` for N seconds and no `Stop` / `StopFailure` arrives, log a warning. That's a sign Claude crashed or the hooks themselves were stripped. Don't try to synthesize a `ready` — just surface it so the user knows.

> **Shipped (2026-05-20):** `IdentityAudit` now tracks `BusyHooksSinceTerminal` per session in `%TEMP%\claude-alert\identity-audit.json`. If a session accumulates more than 20 busy events (≈20 minutes at the 1Hz debounce) without any `Stop` / `StopFailure` / `SessionEnd`, ClaudeHook writes a `STUCK_SESSION` line to `claudehook.log`. The flag is per-session and resets when a terminal event does arrive, so it warns once per stuck-period rather than spamming.

### Hook subscription summary (full set ClaudeHook now writes — 9 entries)

```jsonc
{
  "hooks": {
    "SessionStart":     [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<sh>", "timeout": 5  }] }],
    "UserPromptSubmit": [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<sh>", "timeout": 5  }] }],
    "PreToolUse":       [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<sh>", "timeout": 5  }] }],
    "Notification": [
      { "matcher": "permission_prompt", "hooks": [{ "type": "command", "command": "<sh> --notification-kind permission_prompt", "timeout": 10 }] },
      { "matcher": "idle_prompt",       "hooks": [{ "type": "command", "command": "<sh> --notification-kind idle_prompt",       "timeout": 10 }] },
      { "matcher": "auth_success",      "hooks": [{ "type": "command", "command": "<sh> --notification-kind auth_success",      "timeout": 5  }] }
    ],
    "Stop":             [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<sh>", "timeout": 10 }] }],
    "StopFailure":      [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<sh>", "timeout": 10 }] }],
    "SessionEnd":       [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<sh>", "timeout": 5  }] }]
  }
}
```

**Notification needs per-matcher entries.** Claude's hook payload doesn't carry the matcher subtype; we discover it by installing a separate entry per matcher and threading the kind through as a CLI flag. The bash wrapper forwards extras (`"$@"`) to the exe. `auth_success` is subscribed but the exe drops it before pipe send.

**Matcher notes:** `Stop`, `UserPromptSubmit`, `StopFailure`, `PostToolBatch`, `CwdChanged`, etc. **silently ignore** their matcher field. `"matcher": "*"` is harmless but `""` is also fine on those events. Tool events (`PreToolUse`, `PostToolUse`) DO honor matchers and are case-sensitive.

**Migration:** `--install` removes any pre-existing ClaudeHook entries (matched by `command.StartsWith(shBash)`) before writing the new ones, so upgrading from an older subscription shape doesn't leave stale entries firing in parallel.

---

## Fix list for DesktopNames (prioritized by real data)

Below is the data-driven shortlist of changes that would move resolution rate from where it is today to ~100%. The list is ordered by impact, based on 41 real hook events captured on 2026-05-20. The audit data lives at `%TEMP%\claude-alert\identity-audit.json` if you want to inspect it directly.

### What the data shows

| Metric                        | Value     | Comment |
|-------------------------------|-----------|---------|
| Resolved                      | 34 / 41   | 83% |
| Unresolved                    | 7 / 41    | 17% |
| **All failures are one session** | `f75a006c` / `C:\git\tools\EvolxCli` | 7 hooks, 0 resolved |
| Sessions with 100% resolve    | 7 of 8    | every other session works fine |
| ClaudeHook-side data quality  |           | |
| &nbsp;&nbsp;`sessionId` ok    | 41 / 41   | always present, always usable |
| &nbsp;&nbsp;`cwd` ok          | 40 / 41   | the one bad case was a synthetic test |
| &nbsp;&nbsp;`transcriptPath` ok | 39 / 41 | the two bad cases were synthetic tests |
| &nbsp;&nbsp;`ppid` ok         | 2 / 41    | **39 of 41 hooks arrived with PPID=1** — Git Bash MSYS quirk |
| &nbsp;&nbsp;`vscodePid` ok    | 6 / 41    | follows from PPID=1: chain walk usually can't reach Code.exe |
| DesktopNames behaviour        |           | |
| &nbsp;&nbsp;Cases resolved via `cwd` alone | 28 of 34 | DN's cwd-resolver is already doing most of the work |
| &nbsp;&nbsp;Cases needing `vscodePid` | small minority | not the main resolution path |

**Headline:** `vscodePid` is rarely available because PPID=1 on this Git Bash installation. **DesktopNames resolves successfully whenever `cwd` matches an open workspace's `rootName`.** When it doesn't, we fail — and the failure mode is always the same: a session whose `cwd` is a folder that VSCode doesn't currently consider a workspace root.

### Fix #1 — `cwd` walk-up (HIGH IMPACT — would solve all 7 current failures)

Today, DesktopNames matches `cwd` against the `rootName` of each known VSCode window. When they don't match exactly, DN gives up.

Walk `cwd` upward directory-by-directory, looking for any open VSCode window whose **workspace folder path** equals the current directory at any level of the walk.

Example that fails today:
- `cwd = C:\git\tools\EvolxCli\src\Evolx.Cli` (user launched `claude` from a subfolder).
- DN has a window whose workspace root is `C:\git\tools\EvolxCli`, but its `rootName` is `ev.exe` (because of a `.code-workspace` file). Direct match fails.
- Walking `cwd` up: `Evolx.Cli` → `src` → `EvolxCli`. At `C:\git\tools\EvolxCli`, the path equals an open workspace folder → match. Done.

This single change would solve **all 7 of today's failures** plus the historical `incident_design` failures we saw earlier.

Implementation hint: keep a `{workspaceFolderPath → desktopId}` index alongside the existing `{rootName → desktopId}` map. Rebuild it whenever `VsCodeTracker` notices a window change. The folder path comes from `IVsCodeWindow.WorkspaceFolderPath` or whatever your tracker is calling it.

### Fix #2 — `transcriptPath` as fallback identity (MEDIUM)

Claude's transcript path encodes the workspace it considers canonical:

```
C:\Users\dwrigh\.claude\projects\c--git-tools-EvolxCli-src-Evolx-Cli\<sessionId>.jsonl
                                  ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
                                  cwd with \ → - and : → --
```

Decoding the encoded folder name back to a path gives you a second axis to match against. Useful when:
- The cwd is at a level no workspace is open at (your fix #1 walks *up*; this gives you the original full path even if it's a child of nothing currently open).
- A session previously resolved successfully — DesktopNames can keep `{transcriptPath → lastDesktopId}` so the next hook for the same session goes to the same desktop even if the window has been closed since.

Smaller win than #1, but cheap and addresses the "session that used to resolve now doesn't" case.

### Fix #3 — return `userMessage` in `ok:false` replies (DIAGNOSTIC UX)

ClaudeHook already reads and logs `userMessage` from the reply (see `TrySendToDesktopNames` in `Program.cs`). It just isn't populated yet.

When DN can't resolve, return something a human can act on:

```jsonc
{
  "ok": false,
  "error": "vscode-window-not-found",
  "userMessage": "No open VSCode window matches C:\\git\\tools\\EvolxCli. Open the folder in VSCode to enable desktop notifications for this session."
}
```

ClaudeHook will log it verbatim into `claudehook.log`. If you also want to surface it via DesktopNames' own taskbar UI, that's a DN-side rendering choice — the contract just gives you a stable place to put the text.

### Fix #4 — sticky session-to-desktop binding (RESILIENCE)

Session `f75a006c` resolved successfully yesterday (`name='ev.exe'`), fails today (window closed). Once a session is mapped to a desktop, DN should keep flashing that desktop even after the originating window goes away — until the session ends. Worst case the user sees a flash on the "old" desktop and switches there. Better than missing the alert.

State to add: `{sessionId → {desktopId, lastSeen}}` on DN's side. Expire on `state:idle` (SessionEnd) or after some inactivity window (say 1 hour).

### Fix #5 — use `parentChain` (LOW PRIORITY, given the data)

Today `parentChain` is mostly noise because PPID=1 means the chain we walk is short and doesn't reach Code.exe. If you ever do start using it, the right semantics are: **match any PID in the chain against any tracked VSCode window's process tree**. But the data says this is a low-yield path. Skip until/unless other strategies are exhausted.

### Anti-recommendations

- **Don't make `vscodePid` a primary key.** Available only 15% of the time on this Git Bash setup. Workspace path is what works.
- **Don't ask ClaudeHook to fix PPID=1.** That's an MSYS/winpty-level issue and we already recover via self-walk. Pushing more complexity into the wrapper makes it slower, not better.

---

## Separation of concerns

**ClaudeHook is a reporter.** It captures every fact it can scrape from a Claude Code hook event — process tree, paths, IDs, hook context — and ships it to DesktopNames. It does not interpret. It does not decide whether the session is "trackable" or warn the user about workspace setup.

**DesktopNames is the resolver and the diagnostic surface.** It owns the question "which desktop does this Claude belong to?", the answer "I don't know, here's why and what to do about it", and the UI that surfaces both. It already enumerates VSCode windows, knows workspaces and desktops, and has a place to render warnings (the taskbar overlay it already draws).

This split means:

- Adding a new resolution strategy (cwd-walk-up, workspace-subfolder matching, transcript-path correlation) is a DesktopNames change only — no protocol bump.
- Adding a new diagnostic warning ("you have two windows on the same repo, I'm guessing") is a DesktopNames change only.
- ClaudeHook stays small and stable.

When DesktopNames is unreachable (pipe down — true infrastructure failure), ClaudeHook falls back to a generic Windows balloon — but only for events the user would want to be interrupted by (`Notification` and `Stop`/`StopFailure`). `SessionStart`/`UserPromptSubmit`/`PreToolUse`/`SessionEnd` silently drop on the floor. Everything else is DesktopNames' domain.

---

## Wire contract

### Pipe

- Name: `\\.\pipe\DesktopNames`
- Direction: client (ClaudeHook) writes one JSON message, server (DesktopNames) replies once, then closes.
- Per-user scope. No elevation. No firewall.
- If `Connect(150ms)` fails, the server isn't running — ClaudeHook falls back to a Windows balloon for user-interruptible events only (`Notification`, `Stop`, `StopFailure`). Other events silently drop.

### Request

UTF-8 JSON, newline-terminated. One message per connection. **Extra fields not listed here may appear in future** — DesktopNames should ignore unknown fields (System.Text.Json does this by default).

```jsonc
{
  // --- core ---
  "type": "state",                  // message-type discriminator (legacy name; currently always "state")
  "source": "claude-code",          // which integration is reporting; reserved for future sources
  "title": "Claude — Claude-Alert", // pre-formatted headline for fallback toast
  "body": "Running Edit",           // pre-formatted body for fallback toast

  // --- identity (stable across hooks of the same session) ---
  "sessionId": "abc12345",          // first 8 chars of Claude's session UUID
  "cwd": "c:/git/tools/Adv",        // working directory at hook fire time
  "transcriptPath": "~/.claude/projects/c--git-tools-Adv/abc12345-....jsonl",
                                    // canonical session identity; encodes cwd+session
  "hostname": "NB181",              // for multi-machine setups (remote Claude → local DN)

  // --- hook context (varies by event type; may be null) ---
  "hookEvent": "PreToolUse",        // see "State model" table below for the full event set
  "hookSource": null,               // SessionStart only: "startup" | "resume" | "clear" | "compact"
  "toolName": "Edit",               // PreToolUse / PostToolUse only
  "message": null,                  // Notification only: the question Claude is asking
  "notificationKind": null,         // Notification only: "permission_prompt" | "idle_prompt" | "auth_success"
                                    // (ClaudeHook never sends auth_success — suppressed before pipe send)
  "errorType": null,                // StopFailure only: "rate_limit" | "authentication_failed" |
                                    // "billing_error" | network error names | etc. (raw from Claude)
  "lastMessageEndsWithQuestion":    // Stop only: true if Claude's last assistant text ends with `?`.
      false,                        // null if the transcript was missing/empty.
                                    // If the transcript schema breaks, ClaudeHook leaves both
                                    // lastMessage* fields null AND pops a one-shot Windows toast
                                    // per session telling the user to update ClaudeHook.
  "lastMessageTail": null,          // Stop only: last ~200 chars of Claude's final assistant text
                                    // (so DN can render the question text in a tooltip)
  "backgroundActive": false,        // Stop only: true if background work is still running at
                                    // turn end — a live Monitor watcher (detected from the
                                    // transcript) or a run_in_background Bash shell (from the
                                    // Stop payload's background_tasks). DN colours such a Stop
                                    // orange (working), not green (done). See the colour table.

  // --- process tree (everything we know; DN matches however it wants) ---
  "vscodePid": 22148,               // topmost Code/Cursor in chain, or 0 if not found
  "sessionPid": 27704,              // ClaudeHook.exe's own PID (mostly diagnostic)
  "parentPid": 62268,               // claude.exe (the walk's starting point)
  "parentChain": [                  // every process from claude.exe upward, in order
    { "pid": 62268, "name": "powershell" },
    { "pid": 55088, "name": "claude" },
    { "pid": 78912, "name": "Code" },     // worker
    { "pid": 22148, "name": "Code" },     // window
    { "pid": 3356,  "name": "explorer" }
  ],
  "walkOutcome": "found-code"       // see below
}
```

#### `walkOutcome` values

| Value              | Meaning                                                          |
|--------------------|------------------------------------------------------------------|
| `found-code`       | At least one `Code` / `Cursor` / `Code - Insiders` in the chain. `vscodePid` is set to the topmost match. |
| `no-code-ancestor` | Walked the full chain (or 8 hops), no Code/Cursor seen. Claude is running outside a VSCode-hosted terminal. |
| `chain-broken`     | A hop returned an empty process name (parent already exited before we could query it). |
| `ppid-was-1`       | Git Bash's `$PPID` MSYS quirk — we never had a usable start point. |
| `skipped`          | No `--from-pid` was passed to ClaudeHook. |

`walkOutcome` is meant to be cheap diagnostic context for DesktopNames, *not* a directive. DesktopNames decides what to do — e.g. for `no-code-ancestor` it might warn "this Claude is headless, no desktop binding possible".

### Reply

```jsonc
{ "ok": true, "desktopIndex": 2, "desktopName": "Mobilität" }
```

or

```jsonc
{ "ok": false, "error": "vscode-window-not-found" }
```

#### Suggested richer reply shape (for DesktopNames to consider)

ClaudeHook currently only looks at `ok`. If DesktopNames wants to surface its own diagnostics (the user-facing warnings about workspace setup, etc.), it can return them in the reply — ClaudeHook will log them but take no action.

```jsonc
{
  "ok": false,
  "error": "workspace-mismatch",            // machine-readable code, see suggested codes below
  "userMessage": "Open c:/git/foo as a folder in VSCode for desktop notifications.",
  "warnOnce": true                          // hint: only show in DN's UI once per (sessionId, error)
}
```

Suggested error codes (DesktopNames' choice — not part of ClaudeHook's contract):

| Code                       | Meaning                                                                 |
|----------------------------|-------------------------------------------------------------------------|
| `vscode-window-not-found`  | Couldn't match the payload to any open VSCode window.                   |
| `headless`                 | `walkOutcome` was `no-code-ancestor` and no VSCode window has this cwd — Claude is running in a non-VSCode terminal. |
| `workspace-mismatch`       | A VSCode window exists for some Code PID in the chain, but its workspace doesn't contain the cwd. |
| `workspace-subfolder`      | cwd is a subfolder of an open workspace — could resolve if DN walks cwd up. |
| `no-folder-open`           | VSCode window exists but has no folder loaded.                          |
| `anonymous`                | sessionId was empty or otherwise unusable.                              |

These are suggestions, not contract. ClaudeHook will log whatever `error` it sees verbatim — it doesn't switch on the value.

There is no `ttlSeconds` and no de-dup window. DesktopNames keys state by `sessionId`; the latest message wins. State transitions are idempotent — sending the same `busy` twice is harmless.

---

## Event → colour model

DesktopNames keys on `hookEvent` (and `notificationKind` for Notification events) and decides colour. The user's preferred semantics:

| Claude hook event  | `notificationKind` | Suggested colour | Notes                                                            |
|--------------------|--------------------|------------------|------------------------------------------------------------------|
| `SessionStart`     | n/a                | 🟠 orange         | Claude is starting up — working. `hookSource` tells you `startup`/`resume`/`clear`/`compact`. |
| `UserPromptSubmit` | n/a                | 🟠 orange         | User just hit enter — Claude is about to think. Earlier signal than the first `PreToolUse` (Claude can take seconds before its first tool call). |
| `PreToolUse`       | n/a                | 🟠 orange         | Claude is calling a tool. `toolName` tells you which. ClaudeHook debounces these per `sessionId` to ≤1Hz so DN doesn't get hammered. |
| `Notification`     | `permission_prompt`| 🟡 yellow         | User must approve a tool call. `message` has the question. Stays yellow until next event for the session. |
| `Notification`     | `idle_prompt`      | 🟡 yellow         | Claude went idle waiting for the user. **Same colour as permission_prompt — both mean "Claude is blocked on you"** regardless of how long. Could be minutes, could be hours; user might be at lunch. |
| `Notification`     | `auth_success`     | (never arrives)   | ClaudeHook suppresses these before pipe send — they're not user-actionable. |
| `Stop`             | n/a                | 🟢 green          | Turn finished cleanly. Claude is dormant. **Unless `backgroundActive: true`** → 🟠 orange: the turn ended but a Monitor watcher or `run_in_background` Bash is still running, so Claude is still working, not done. |
| `StopFailure`      | n/a                | 🔴 red (suggested)| Turn ended with an error. `errorType` has the kind (`rate_limit`, `authentication_failed`, `billing_error`, network errors, etc.). Same "Claude is finished" semantic as green but worth a more attention-getting colour because the user may need to act (refresh credentials, wait, switch model). |
| `SessionEnd`       | n/a                | (clear indicator) | Session ended. DN should clear the per-session state. `hookSource` tells you why (`clear`/`resume`/`logout`/`prompt_input_exit`/`other`). |

> **Key semantic distinction:** *finished* (green/red — Claude is dormant) ≠ *waiting on user* (yellow — Claude is blocked). Yellow must stay yellow regardless of how long it's been showing. That's the whole point.

### Flavours of "Claude is asking" — all now detected

There used to be a stuck-orange gap when Claude asked an inline `y/n` and `Notification` didn't fire. The 2026-05-20 build closes it by subscribing to `PermissionRequest` (the hook that actually fires when the inline dialog appears) and by exposing `toolName` so DN can detect blocking tools.

| What's on Claude's screen | Hook fired | How DN colours yellow |
|---|---|---|
| **Inline `y/n` prompt** for a sensitive Bash/Edit operation | `PermissionRequest` (tool name in matcher) | `hookEvent == "PermissionRequest"` |
| **Structured choice** (radio-button question, multi-select) | `PreToolUse` with `toolName: "AskUserQuestion"` | `hookEvent == "PreToolUse" && toolName == "AskUserQuestion"` |
| **Plan-approval prompt** after Claude's planning phase | `PreToolUse` with `toolName: "ExitPlanMode"` | `hookEvent == "PreToolUse" && toolName == "ExitPlanMode"` |
| **System notification / Windows toast** for a permission prompt | `Notification` with `permission_prompt` | `notificationKind == "permission_prompt"` |
| **Idle prompt** (Claude waited too long for input) | `Notification` with `idle_prompt` | `notificationKind == "idle_prompt"` |
| **Chat-text question** ("Should I proceed with X?" as a normal response) | `Stop` + transcript peek | `hookEvent == "Stop" && lastMessageEndsWithQuestion == true`. ClaudeHook reads the tail of the transcript JSONL on every Stop, extracts the assistant's last text content, sets the field. Heuristic is "ends with `?` after trimming trailing whitespace/quotes/parens" — ~80% precision. Schema break (Anthropic changes the transcript format) → toast to user, leave the fields null. |

**Multi-Claude priority** on a single desktop, highest wins: `🟡 yellow > 🟠 orange > 🔴 red > 🟢 green`. Rationale: yellow demands immediate attention; orange means active work; red means an error happened but Claude isn't blocking; green is the resting state.

**For diagnosing a desktop colour you don't expect**, run this in PowerShell:

```powershell
$log = "$env:TEMP\claude-alert\claudehook.log"
$session = '8eb30580'   # ← short session id from DN tooltip
Get-Content $log | Select-String "session=$session" |
    Select-Object -Last 10 |
    ForEach-Object {
        if ($_.Line -match '(\d{2}:\d{2}:\d{2}).*hook event=(\w+)(.*tool=(\S+))?') {
            "$($matches[1])  $($matches[2])  $($matches[4])"
        }
    }
```

The last line tells you which event ClaudeHook last sent for that session, and which tool (if any) it was about. Cross-reference with the colour table above.

Safety net: if neither `Stop`/`StopFailure` nor `SessionEnd` fires (Claude crashed mid-turn), DN can sweep using `sessionPid` and `Process.HasExited` to time out stale state. ClaudeHook also writes a `STUCK_SESSION` log line into `claudehook.log` if a session accumulates 20+ busy-events without any terminal event — useful canary that hooks got stripped or events got dropped.

---

## Hook wiring (the ClaudeHook side, for reference)

ClaudeHook's install path (`ClaudeHook.exe --install`) writes 9 hook subscriptions into `~/.claude/settings.json`. Per-matcher entries for `Notification` let ClaudeHook know which subtype fired without depending on Claude putting that information in the payload:

```jsonc
{
  "hooks": {
    "SessionStart":     [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<path>/claude-alert.sh",                                          "timeout": 5  }] }],
    "UserPromptSubmit": [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<path>/claude-alert.sh",                                          "timeout": 5  }] }],
    "PreToolUse":       [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<path>/claude-alert.sh",                                          "timeout": 5  }] }],
    "Notification": [
      { "matcher": "permission_prompt", "hooks": [{ "type": "command", "command": "<path>/claude-alert.sh --notification-kind permission_prompt", "timeout": 10 }] },
      { "matcher": "idle_prompt",       "hooks": [{ "type": "command", "command": "<path>/claude-alert.sh --notification-kind idle_prompt",       "timeout": 10 }] },
      { "matcher": "auth_success",      "hooks": [{ "type": "command", "command": "<path>/claude-alert.sh --notification-kind auth_success",      "timeout": 5  }] }
    ],
    "Stop":             [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<path>/claude-alert.sh",                                          "timeout": 10 }] }],
    "StopFailure":      [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<path>/claude-alert.sh",                                          "timeout": 10 }] }],
    "SessionEnd":       [{ "matcher": "*", "hooks": [{ "type": "command", "command": "<path>/claude-alert.sh",                                          "timeout": 5  }] }]
  }
}
```

The bash wrapper hands the exe `$PPID` (claude.exe — stable for the session) and forwards any extra args via `"$@"`, which is how the per-matcher Notification entries pass `--notification-kind <subtype>` through to the exe:

```bash
INPUT=$(cat)
printf '%s' "$INPUT" | "$EXE_DIR/ClaudeHook.exe" --from-pid "$PPID" "$@" </dev/stdin
```

The exe walks parents from there via `NtQueryInformationProcess` (~2ms per hop, ~10ms typical). The walk identifies the topmost Code/Cursor process in the chain — that's `vscodePid`.

> **Notification matchers** — Claude Code's hook payload doesn't carry the matcher subtype as a field; we discover which subtype fired by installing a separate hook entry per matcher and passing the kind as a CLI flag. `auth_success` is subscribed but ClaudeHook short-circuits before pipe send (no point bothering DN with "auth completed OK" notifications).
>
> **Stop and StopFailure are both "Claude is finished".** DN should clear orange in both cases. Per the user's colour scheme: `Stop → green` (clean finish), `StopFailure → red` (finished with error, `errorType` has the kind). See the migration section at the top of this doc for the full event→colour table.

---

## Latency budget (current measurements)

- Pipe absent (DN not running): 150ms `Connect` timeout → balloon path.
- Pipe present, walk cache hit: ~5–20ms end to end.
- Pipe present, walk cache miss (e.g. first hook of a new session): ~50–110ms end to end.

The 1Hz `busy` debounce in ClaudeHook ensures `PreToolUse` storms don't spam the pipe — DN gets at most one busy message per session per second.

---

## Logs (for debugging mismatches)

ClaudeHook writes one log per process to `%TEMP%\claude-alert\claudehook.log` (rotates at 512KB). Each hook fires a block like:

```
15:36:28.463 pid=27704     +9ms  START
15:36:28.516 pid=27704    +55ms  hook event=PreToolUse session=richpayl cwd=c:/git/tools/Claude-Alert -> state=busy
15:36:28.524 pid=27704    +62ms    walk from 62268: 5 hops, outcome=found-code, vscodePid=22148, 5ms
15:36:28.536 pid=27704    +74ms  OUT {"type":"state",...,"parentChain":[...],"walkOutcome":"found-code"}
15:36:28.540 pid=27704    +79ms    pipe connected in 1ms
15:36:28.569 pid=27704   +107ms    pipe reply='{"ok":true,"desktopIndex":2,"desktopName":"Mobilität"}' total=29ms
15:36:28.571 pid=27704   +109ms    RESOLVED desktop=2 name='Mobilität' (DesktopNames decided this from the payload above)
```

Useful greps:

- `Select-String -Path … -Pattern 'RESOLVED|UNRESOLVED'` — one line per alert showing what DN picked.
- `Select-String -Path … -Pattern 'OUT '` — every payload sent (full JSON).
- `Select-String -Path … -Pattern 'walkOutcome'` — which sessions can/can't be resolved by process-tree walking.

If you suspect a mismatch ("DN picked desktop X but I'm working on Y"), the `OUT` line shows exactly what payload led to that decision — the `cwd`, the full `parentChain`, the `vscodePid` — so DN-side debugging starts from the same evidence ClaudeHook has.

---

## Open questions for the DesktopNames side

(The big resolution-rate ones — cwd walk-up, sticky bindings, userMessage — are in the prioritized fix list at the top of this doc. What remains here are genuine design choices that the data doesn't decide for you.)

1. **Warning UX.** Where on the taskbar overlay does a "this Claude isn't trackable" warning live? Per-desktop? In a separate icon? Or only via `userMessage` in the reply, surfaced through ClaudeHook's log? Open question.
2. **Dedup state for warnings.** Once you have `userMessage`, how often should the same one re-surface? Suggestion: key by `(sessionId, error)` and only re-warn after a `state:idle` clears the session.
3. **Multi-window collisions.** Two VSCode windows open on the same workspace folder — does DN flash both, prompt the user once which one is "this" Claude, or always pick the most-recently-active one? Not currently observed in the logs but plausible.
