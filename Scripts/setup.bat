@echo off
rem One-shot setup: build and register mcp-dotNET with Claude Code, Claude Desktop and Codex (stdio).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo %cmdcmdline% | find /i "/c" >nul && pause
