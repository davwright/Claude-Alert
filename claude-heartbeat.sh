#!/bin/bash
# Claude Heartbeat Hook — writes per-session status JSON to a temp directory.
# DevPulse (or any watcher) scans %TEMP%/claude-heartbeats/*.json for all sessions.
# Must be FAST — runs on every PreToolUse/PostToolUse.

INPUT=$(cat)
EVENT=$(echo "$INPUT" | sed -n 's/.*"hook_event_name" *: *"\([^"]*\)".*/\1/p')
TOOL=$(echo "$INPUT" | sed -n 's/.*"tool_name" *: *"\([^"]*\)".*/\1/p')
SESSION=$(echo "$INPUT" | sed -n 's/.*"session_id" *: *"\([^"]*\)".*/\1/p')
CWD=$(echo "$INPUT" | sed -n 's/.*"cwd" *: *"\([^"]*\)".*/\1/p')

# Each session gets its own file in a shared directory
HEARTBEAT_DIR="$TEMP/claude-heartbeats"
mkdir -p "$HEARTBEAT_DIR" 2>/dev/null

# Use session ID as filename (falls back to "unknown" if empty)
SESS_FILE="${SESSION:-unknown}"
HEARTBEAT_FILE="$HEARTBEAT_DIR/$SESS_FILE.json"

# ── PID resolution (cached per session) ──────────────────────
# $PPID in Git Bash on Windows returns 1 (useless). Instead, walk up the
# process tree via wmic on the first heartbeat and cache the result.
# Subsequent calls read the cached file (~0ms vs ~400ms for wmic).
PID_CACHE="$HEARTBEAT_DIR/$SESS_FILE.pid"
if [[ -f "$PID_CACHE" ]]; then
  # Read cached PIDs (format: claude_pid:vscode_pid)
  IFS=: read -r CLAUDE_PID VSCODE_PID < "$PID_CACHE"
else
  # First heartbeat for this session — resolve via wmic (background-safe)
  CLAUDE_PID=0
  VSCODE_PID=0
  # Walk from this bash process ($PPID) up through the tree to find claude.exe
  # On Windows the tree is: Code.exe(window) > Code.exe(worker) > claude.exe > bash > this script
  CUR_PID="$PPID"
  for _ in 1 2 3 4 5; do
    LINE=$(wmic process where "ProcessId=$CUR_PID" get Name,ParentProcessId /value 2>/dev/null | tr -d '\r')
    P_NAME=$(echo "$LINE" | sed -n 's/^Name=//p')
    P_PPID=$(echo "$LINE" | sed -n 's/^ParentProcessId=//p')
    if [[ "$P_NAME" == "claude.exe" ]]; then
      CLAUDE_PID="$CUR_PID"
      # Parent of claude.exe is a Code.exe worker; grandparent is the VS Code window
      GP_LINE=$(wmic process where "ProcessId=$P_PPID" get ParentProcessId /value 2>/dev/null | tr -d '\r')
      VSCODE_PID=$(echo "$GP_LINE" | sed -n 's/^ParentProcessId=//p')
      VSCODE_PID="${VSCODE_PID:-0}"
      break
    fi
    [[ -z "$P_PPID" || "$P_PPID" == "0" ]] && break
    CUR_PID="$P_PPID"
  done
  echo "${CLAUDE_PID:-0}:${VSCODE_PID:-0}" > "$PID_CACHE" 2>/dev/null
fi

TS=$(date +%s%3N 2>/dev/null || echo "0")

# DevPulse work-item link (set by ClaudeManager when spawning)
WI_ID="${DEVPULSE_WORK_ITEM_ID:-}"
WI_ORG="${DEVPULSE_ORG:-}"
WI_PROJECT="${DEVPULSE_PROJECT:-}"
WI_PROVIDER="${DEVPULSE_PROVIDER:-}"

case "$EVENT" in
  PreToolUse|PostToolUse|SubagentStart)
    STATUS="thinking"
    ;;
  Notification)
    STATUS="waiting"
    ;;
  Stop|SessionEnd)
    STATUS="idle"
    ;;
  UserPromptSubmit)
    STATUS="thinking"
    ;;
  *)
    STATUS="active"
    ;;
esac

# Write atomically (write tmp then rename) to avoid partial reads
TMP_FILE="$HEARTBEAT_FILE.tmp"
printf '{"status":"%s","event":"%s","tool":"%s","session":"%s","cwd":"%s","ts":%s,"pid":%s,"vscodePid":%s,"workItemId":"%s","org":"%s","project":"%s","provider":"%s"}\n' \
  "$STATUS" "$EVENT" "$TOOL" "$SESSION" "$CWD" "$TS" "${CLAUDE_PID:-0}" "${VSCODE_PID:-0}" "$WI_ID" "$WI_ORG" "$WI_PROJECT" "$WI_PROVIDER" > "$TMP_FILE"
mv -f "$TMP_FILE" "$HEARTBEAT_FILE" 2>/dev/null || cp -f "$TMP_FILE" "$HEARTBEAT_FILE"

# Also write the legacy single file for backwards compat
printf '{"status":"%s","event":"%s","tool":"%s","session":"%s","cwd":"%s","ts":%s,"pid":%s,"vscodePid":%s,"workItemId":"%s","org":"%s","project":"%s","provider":"%s"}\n' \
  "$STATUS" "$EVENT" "$TOOL" "$SESSION" "$CWD" "$TS" "${CLAUDE_PID:-0}" "${VSCODE_PID:-0}" "$WI_ID" "$WI_ORG" "$WI_PROJECT" "$WI_PROVIDER" > "$HEARTBEAT_DIR/../claude-heartbeat.json" 2>/dev/null

exit 0
