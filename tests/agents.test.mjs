// End-to-end against the running DesktopNames: a Stop forwards its background task list with each
// task's start time (from its tasks/<id>.output file); a background agent's PreToolUse reaches DN
// tagged with the agent (listed, colour untouched), its PostToolUse is dropped, SubagentStop removes it.
// Usage: node tests/agents.test.mjs [path/to/ClaudeHook.exe]
import { spawnSync } from "node:child_process";
import { readFileSync, mkdirSync, writeFileSync, rmSync, mkdtempSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

const exe = process.argv[2] ?? new URL("../src/bin/Release/net8.0-windows/ClaudeHook.exe", import.meta.url).pathname.slice(1);
const dnLog = process.env.APPDATA + "/DesktopNames/desktopnames.log";
const hookLog = process.env.TEMP + "/claude-alert/claudehook.log";
const session = "agtest-" + (process.pid % 10);             // ClaudeHook sends the first 8 chars
const agent = "a" + process.pid;
const send = o => spawnSync(exe, [], { input: JSON.stringify({ session_id: session, cwd: "C:\\git\\tools\\DesktopNames", ...o }), timeout: 30000 });
const logLines = () => readFileSync(dnLog, "utf8").split("\n").filter(l => l.includes(`session=${session}`));
const hookLines = () => readFileSync(hookLog, "utf8").split("\n");

// A session dir like Claude Code's: tasks/<id>.output is created when a background task starts.
const sessDir = mkdtempSync(join(tmpdir(), "agtest-"));
mkdirSync(join(sessDir, "scratchpad"));
mkdirSync(join(sessDir, "tasks"));
writeFileSync(join(sessDir, "tasks", "b1.output"), "");

const checks = [];
send({ hook_event_name: "UserPromptSubmit" });
const stopAt = logLines().length, hookAt = hookLines().length;
send({ hook_event_name: "Stop", scratchpad_dir: join(sessDir, "scratchpad"), background_tasks: [
  { id: agent, type: "subagent", status: "running", description: "Runbook" },
  { id: "b1", type: "shell", status: "running", description: "Serve on localhost" }] });
const out = hookLines().slice(hookAt).find(l => l.includes(" OUT ") && l.includes(`"sessionId":"${session}"`)) ?? "";
checks.push(["shell start time read from its task file", /"id":"b1"[^}]*"startedUtc":"20/.test(out)]);
checks.push(["no task file -> no start time, not a guess", new RegExp(`"id":"${agent}"[^}]*"startedUtc":null`).test(out)]);
checks.push(["Stop forwards its background task list", logLines().slice(stopAt).some(l => l.includes("hook=Stop") && l.includes(" bg=2"))]);

const before = logLines().length;
send({ hook_event_name: "PreToolUse", tool_name: "Bash", tool_input: { command: "ls", description: "List files" }, agent_id: agent, agent_type: "general-purpose" });
send({ hook_event_name: "PostToolUse", tool_name: "Bash", tool_input: { command: "ls" }, agent_id: agent, agent_type: "general-purpose" });
let fresh = logLines().slice(before);
checks.push(["agent PreToolUse recorded on the known session",
  fresh.some(l => l.includes(`agent=general-purpose/${agent} PreToolUse sessionKnown=True`))]);
checks.push(["agent events never change the session state", !fresh.some(l => /state\s+applied/.test(l))]);
checks.push(["agent PostToolUse dropped", !fresh.some(l => l.includes("PostToolUse"))]);

const mid = logLines().length;
send({ hook_event_name: "SubagentStop", agent_id: agent, agent_type: "general-purpose" });
fresh = logLines().slice(mid);
checks.push(["SubagentStop recorded", fresh.some(l => l.includes(`agent=general-purpose/${agent} SubagentStop sessionKnown=True`))]);

send({ hook_event_name: "SessionEnd" });
rmSync(sessDir, { recursive: true, force: true });
let bad = 0;
for (const [name, ok] of checks) { if (!ok) bad++; console.log(`${ok ? "ok  " : "FAIL"} ${name}`); }
if (bad) console.log(out.slice(0, 600) + "\n" + logLines().slice(-12).join("\n"));
console.log(bad ? `${bad} failed` : "all passed");
process.exit(bad ? 1 : 0);
