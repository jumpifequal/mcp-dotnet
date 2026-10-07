@echo off
rem Start the mcp-dotNET console (build / start-stop HTTP server / status / tests).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\console.ps1" %*
echo %cmdcmdline% | find /i "/c" >nul && pause
