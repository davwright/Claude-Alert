#!/bin/bash
# Claude Alert hook wrapper - launches the PowerShell alert script
# Uses Start-Process to fully detach so it survives parent shell exit
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell.exe -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File','C:/git/tools/Claude-Alert/claude-alert.ps1' -WindowStyle Hidden"
