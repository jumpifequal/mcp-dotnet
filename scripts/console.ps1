#requires -version 5
<#
.SYNOPSIS
  Interactive manager for the mcp-dotNET server: build, start/stop the HTTP transport, health check,
  generate a token, run the tests. For stdio there is nothing to start — the client launches the server.
#>
[CmdletBinding()]
param(
    [int]$Port = 5005,
    [switch]$ReadOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/MCPPOC/MCPPOC.csproj'
# Repo layout uses src/.../bin; a distribution package ships the binary under .\app\.
$distDll = Join-Path $repo 'app/mcp-dotnet.dll'
$dll = if (Test-Path $distDll) { $distDll } else { Join-Path $repo 'src/MCPPOC/bin/Release/net10.0/mcp-dotnet.dll' }
$script:proc = $null
$script:token = $null
$script:logFile = Join-Path $env:TEMP "mcp-dotnet-console.log"

function Build {
    Write-Host '[*] Building (Release)...' -ForegroundColor Cyan
    dotnet build $project -c Release
}
function Ensure-Dll { if (-not (Test-Path $dll)) { Write-Warning 'Server not built yet. Choose Build first.'; return $false }; return $true }

function Start-Server {
    if (-not (Ensure-Dll)) { return }
    if ($script:proc -and -not $script:proc.HasExited) { Write-Warning "Already running (PID $($script:proc.Id))."; return }
    $script:token = (& dotnet $dll --gen-token).Trim()
    $env:MCP_DOTNET_HTTP_TOKEN = $script:token
    $args = @($dll, '--transport', 'http', '--host', '127.0.0.1', '--port', "$Port", '--log-level', 'Information')
    if ($ReadOnly) { $args += '--read-only' }
    if (Test-Path $script:logFile) { Remove-Item $script:logFile }
    $script:proc = Start-Process dotnet -ArgumentList $args -PassThru -NoNewWindow -RedirectStandardError $script:logFile
    Start-Sleep -Milliseconds 1500
    Write-Host "[+] Started PID $($script:proc.Id)" -ForegroundColor Green
    Write-Host "    endpoint: http://127.0.0.1:$Port/mcp"
    Write-Host "    token   : $script:token"
    Write-Host "    For Claude Code: claude mcp add dotnet --transport http http://127.0.0.1:$Port/mcp --header `"Authorization: Bearer $script:token`""
}
function Stop-Server {
    if ($script:proc -and -not $script:proc.HasExited) { $script:proc.Kill(); Write-Host '[+] Stopped.' -ForegroundColor Green }
    else { Write-Host '[=] Not running.' }
    $script:proc = $null
}
function Status {
    if ($script:proc -and -not $script:proc.HasExited) {
        Write-Host "[=] Running, PID $($script:proc.Id), port $Port" -ForegroundColor Green
        try {
            $init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"console","version":"1"}}}'
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/mcp" -Method Post -Body $init `
                -Headers @{ Authorization = "Bearer $script:token"; Accept = 'application/json, text/event-stream' } `
                -ContentType 'application/json' -UseBasicParsing -TimeoutSec 5
            Write-Host "    health: HTTP $($r.StatusCode) (initialize ok)" -ForegroundColor Green
        } catch { Write-Warning "    health check failed: $($_.Exception.Message)" }
    } else { Write-Host '[=] Not running.' }
}
function Tail-Log {
    if (Test-Path $script:logFile) { Get-Content $script:logFile -Tail 30 } else { Write-Host '(no log yet)' }
}

function Menu {
    Write-Host ''
    Write-Host ' mcp-dotNET console' -ForegroundColor White
    Write-Host ' ------------------'
    Write-Host '  [B] Build (Release)'
    Write-Host '  [S] Start HTTP server      [X] Stop server'
    Write-Host '  [I] Status / health        [L] Tail log'
    Write-Host '  [G] Generate token         [T] Run tests'
    Write-Host '  [Q] Quit'
}

try {
    while ($true) {
        Menu
        $c = (Read-Host 'choice').Trim().ToUpper()
        switch ($c) {
            'B' { Build }
            'S' { Start-Server }
            'X' { Stop-Server }
            'I' { Status }
            'L' { Tail-Log }
            'G' { if (Ensure-Dll) { Write-Host (& dotnet $dll --gen-token).Trim() -ForegroundColor Yellow } }
            'T' { dotnet test (Join-Path $repo 'mcp-dotnet.slnx') -c Release }
            'Q' { break }
            default { }
        }
    }
} finally {
    Stop-Server
}
