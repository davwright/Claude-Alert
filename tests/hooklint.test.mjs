// --lint-hooks flags shell-form hooks (no "args") in a project's settings and leaves exec-form ones alone.
// Usage: node tests/hooklint.test.mjs [path/to/ClaudeHook.exe]
import { spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

const exe = process.argv[2] ?? new URL("../src/bin/Release/net8.0-windows/ClaudeHook.exe", import.meta.url).pathname.slice(1);
const proj = mkdtempSync(join(tmpdir(), "hooklint-"));
mkdirSync(join(proj, ".claude"));
const hook = (command, extra = {}) => ({ hooks: [{ type: "command", command, ...extra }] });
writeFileSync(join(proj, ".claude", "settings.json"), JSON.stringify({ hooks: {
  PreToolUse: [hook("C:/x/slow-guard.sh"), hook("node C:/x/shell-node.mjs"), hook("node", { args: ["C:/x/fast.mjs"] })],
  Stop: [hook("C:/x/fast.exe", { args: [] }), { hooks: [{ type: "http", url: "http://localhost:1" }] }],
} }));

const r = spawnSync(exe, ["--lint-hooks", proj], { encoding: "utf8", timeout: 30000 });
const mine = r.stdout.split("\n").filter(l => l.startsWith("project settings"));
const checks = [
  ["exit 1 when a shell hook exists", r.status === 1],
  [".sh flagged", mine.some(l => l.includes("slow-guard.sh"))],
  ["command line without args flagged", mine.some(l => l.includes("shell-node.mjs"))],
  ["exec-form node not flagged", !mine.some(l => l.includes("fast.mjs"))],
  ["exec-form exe not flagged", !mine.some(l => l.includes("fast.exe"))],
  ["exactly two project findings", mine.length === 2],
];
rmSync(proj, { recursive: true, force: true });
let bad = 0;
for (const [name, ok] of checks) { if (!ok) bad++; console.log(`${ok ? "ok  " : "FAIL"} ${name}`); }
if (bad) console.log(r.stdout);
console.log(bad ? `${bad} failed` : "all passed");
process.exit(bad ? 1 : 0);
