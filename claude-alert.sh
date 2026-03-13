#!/bin/bash
# Claude Alert hook wrapper - launches the PowerShell alert script
# Runs detached so it doesn't block Claude's hook timeout
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "C:/git/tools/Claude-Alert/claude-alert.ps1" &
