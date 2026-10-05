# Claude-Alert

## Hooks never run through a shell

On this machine Git Bash takes ~1.5s to start idle and 5-12s under load (about 6 hooks spawn per tool call, across every open session). Claude Code cancels a hook at its timeout, so a shell hook silently drops events: on 2026-10-05 DesktopNames missed whole turns, and more than 2000 hook runs were cancelled in one day.

- ClaudeHook is wired in exec form: `"command": "<path>/ClaudeHook.exe", "args": [...], "async": true`. `--install` writes exactly that. Never reintroduce a `.sh` wrapper.
- DevPulse's heartbeat (`%TEMP%/claude-heartbeats/<session>.json`) is written by ClaudeHook (`src/Heartbeat.cs`). It was `claude-heartbeat.sh`.
- Anything new that has to run per hook event goes into ClaudeHook, not into a new hook.
- `ClaudeHook.exe --lint-hooks` lists every shell-form hook (user, project, enabled plugins) and exits 1; SessionStart runs it and toasts the findings.

## Tests before deploy

`node tests/heartbeat.test.mjs` and `node tests/hooklint.test.mjs` against the build, then `publish.ps1`, then `release\ClaudeHook.exe --install`.
