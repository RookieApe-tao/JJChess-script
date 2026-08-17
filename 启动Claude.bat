@echo off
REM 通过 Windows Terminal 启动 Claude Code
REM 使用 PowerShell 配置文件以获得最佳兼容性

wt.exe -p "PowerShell" --title "Claude Code" cmd.exe /k "cd /d D:\Trae CN\project\xiangqi-assistant && claude --dangerously-skip-permissions"
