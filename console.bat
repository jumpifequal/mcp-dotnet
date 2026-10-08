@echo off
rem Start the mcp-dotNET console: the WPF GUI (build/start/stop/profiles/log/Problems/Integration)
rem when available, building it on first run; falls back to the text console (scripts\console.ps1)
rem if the GUI can't be built (e.g. the Windows Desktop Runtime isn't installed).
setlocal
set "HERE=%~dp0"
set "GUI_EXE=%HERE%src\MCPPOC.Console\bin\Release\net10.0-windows\mcp-dotnet-console.exe"

if exist "%GUI_EXE%" goto :launch_gui

where dotnet >nul 2>&1 || goto :fallback
echo Building the GUI console (first run only)...
dotnet build "%HERE%src\MCPPOC.Console\MCPPOC.Console.csproj" -c Release >nul 2>&1
if exist "%GUI_EXE%" goto :launch_gui

:fallback
echo GUI console unavailable; using the text console instead.
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%scripts\console.ps1" %*
echo %cmdcmdline% | find /i "/c" >nul && pause
exit /b

:launch_gui
start "" "%GUI_EXE%"
exit /b
