// End-to-end: feed hook events to a built ClaudeHook.exe and check the DevPulse heartbeat it writes.
// Usage: node tests/heartbeat.test.mjs [path/to/ClaudeHook.exe]   (default: src/bin/Release build)
import { spawnSync } from "node:child_process";
import { readFileSync, rmSync, existsSync, readdirSync } from "node:fs";
import { join } from "node:path";

const exe = process.argv[2] ?? new URL("../src/bin/Release/net8.0-windows/ClaudeHook.exe", import.meta.url).pathname.slice(1);
const dir = join(process.env.TEMP, "claude-heartbeats");
const session = "hbtest-" + process.pid;
const file = join(dir, `${session}.json`);

const cases = [
  ["PreToolUse", "thinking"], ["PostToolUse", "thinking"], ["UserPromptSubmit", "thinking"],
  ["Notification", "waiting"], ["Stop", "idle"],
  ["SessionStart", null], ["PermissionRequest", null],   // never reached DevPulse via the .sh either
];
let bad = 0;
for (const [event, want] of cases) {
  rmSync(file, { force: true });
  const input = JSON.stringify({ hook_event_name: event, session_id: session, cwd: "C:\\git\\x", tool_name: "Bash", agent_id: "sub1" });
  const r = spawnSync(exe, [], { input, encoding: "utf8", timeout: 15000, env: { ...process.env, DEVPULSE_WORK_ITEM_ID: "PS-1" } });
  const hb = existsSync(file) ? JSON.parse(readFileSync(file, "utf8")) : null;
  const ok = r.status === 0 && (want === null ? hb === null
    : hb?.status === want && hb.event === event && hb.tool === "Bash" && hb.session === session &&
      hb.cwd === "C:\\git\\x" && hb.ts > 0 && hb.workItemId === "PS-1" && "pid" in hb && "vscodePid" in hb);
  if (!ok) bad++;
  console.log(`${ok ? "ok  " : "FAIL"} ${event} exit=${r.status}${r.error ? " " + r.error.code : ""} -> ${hb ? JSON.stringify(hb) : "(no heartbeat)"}`);
}
// The events also reached DesktopNames as a live session; end it so no test row lingers there.
spawnSync(exe, [], { input: JSON.stringify({ hook_event_name: "SessionEnd", session_id: session, cwd: "C:\\git\\x" }), timeout: 15000 });
rmSync(file, { force: true });
rmSync(join(process.env.TEMP, "claude-alert", `vscode-pid-${session}.json`), { force: true });
const stray = readdirSync(dir).filter(f => f.startsWith(session));
if (stray.length) { bad++; console.log(`FAIL leftover files: ${stray.join(", ")}`); }
console.log(bad ? `${bad} failed` : "all passed");
process.exit(bad ? 1 : 0);
