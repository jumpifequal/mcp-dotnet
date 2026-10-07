#requires -version 5
<#
.SYNOPSIS
  Installs mcp-dotNET on Windows: builds the server (Release) and optionally registers it with
  Claude Code, Claude Desktop and/or Codex.

.DESCRIPTION
  mcp-dotNET is a .NET MCP server, so "install" means build + client registration (there is no venv).
  Configuration files are merged, never overwritten, and a timestamped backup is made first.

.PARAMETER Target
  Which client(s) to configure: claude-code, claude-desktop, codex, or all (default).

.PARAMETER Transport
  stdio (default) or http. stdio works for every client; http registers a URL + bearer header
  (Claude Code and other HTTP-capable clients only).

.PARAMETER ReadOnly
  Register with --read-only (disables patch/rename/save tools).

.PARAMETER Port
  Port for -Transport http (default 5005).

.PARAMETER Name
  MCP server name to register (default 'dotnet').

.PARAMETER NoBuild
  Skip the build; use an existing Release binary.

.PARAMETER NoRegister
  Build only; do not touch any client configuration.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -Target codex -ReadOnly
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -Transport http -Target claude-code
#>
[CmdletBinding()]
param(
    [ValidateSet('claude-code', 'claude-desktop', 'codex', 'all')][string]$Target = 'all',
    [ValidateSet('stdio', 'http')][string]$Transport = 'stdio',
    [switch]$ReadOnly,
    [int]$Port = 5005,
    [string]$Name = 'dotnet',
    [switch]$NoBuild,
    [switch]$NoRegister
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$project = Join-Path $repo 'src/MCPPOC/MCPPOC.csproj'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# 1. Build ------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK not found. Install .NET 10: winget install --id Microsoft.DotNet.SDK.Preview'
}
# A distribution package ships the published binary under .\app\ and no source tree.
$distDll = Join-Path $repo 'app/mcp-dotnet.dll'
$repoDll = Join-Path $repo 'src/MCPPOC/bin/Release/net10.0/mcp-dotnet.dll'
$isDistPackage = Test-Path $distDll

if (-not $NoBuild -and -not $isDistPackage) {
    if (-not (Test-Path $project)) { throw 'No source tree and no published app found; cannot build.' }
    Step 'Building mcp-dotNET (Release)...'
    dotnet build $project -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$dll = if ($isDistPackage) { $distDll } else { $repoDll }
if (-not (Test-Path $dll)) { throw "Server binary not found at $dll. Run without -NoBuild first." }
$dll = (Resolve-Path $dll).Path
Write-Host "    binary: $dll"

if ($NoRegister) { Write-Host 'Build done (no registration requested).' -ForegroundColor Green; return }

$commonArgs = @(); if ($ReadOnly) { $commonArgs += '--read-only' }
$httpToken = $null
if ($Transport -eq 'http') {
    $httpToken = (& dotnet $dll --gen-token).Trim()
    Write-Host "    http endpoint: http://127.0.0.1:$Port/mcp" -ForegroundColor Yellow
    Write-Host "    bearer token : $httpToken  (set MCP_DOTNET_HTTP_TOKEN before starting the server)" -ForegroundColor Yellow
}

function Backup($path) {
    if (Test-Path $path) { $b = "$path.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"; Copy-Item $path $b; Write-Host "    backup: $b" }
}

function Merge-JsonServer($path, [object]$entry) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $root = if (Test-Path $path) { Get-Content $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (-not $root.PSObject.Properties['mcpServers']) { $root | Add-Member mcpServers ([pscustomobject]@{}) }
    if ($root.mcpServers.PSObject.Properties[$Name]) { $root.mcpServers.PSObject.Properties.Remove($Name) }
    $root.mcpServers | Add-Member -NotePropertyName $Name -NotePropertyValue $entry
    Backup $path
    $root | ConvertTo-Json -Depth 20 | Set-Content -Path $path -Encoding UTF8
    Write-Host "    wrote: $path" -ForegroundColor Green
}

# stdio entry: launch the DLL through the shared dotnet host.
$stdioEntry = [pscustomobject]@{ command = 'dotnet'; args = @($dll) + $commonArgs }
# http entry: URL + bearer header (client connects to an already-running server).
$httpEntry = [pscustomobject]@{ type = 'http'; url = "http://127.0.0.1:$Port/mcp"; headers = [pscustomobject]@{ Authorization = "Bearer $httpToken" } }
$entry = if ($Transport -eq 'http') { $httpEntry } else { $stdioEntry }

function Configure-ClaudeCode {
    Step 'Claude Code'
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        if ($Transport -eq 'http') {
            & claude mcp add $Name --transport http "http://127.0.0.1:$Port/mcp" --header "Authorization: Bearer $httpToken"
        } else {
            & claude mcp add $Name -- dotnet $dll @commonArgs
        }
        if ($LASTEXITCODE -ne 0) { Write-Warning 'claude mcp add failed; writing .mcp.json instead.'; Merge-JsonServer (Join-Path $repo '.mcp.json') $entry }
    } else {
        Write-Warning "'claude' CLI not found; writing project-scoped .mcp.json in the repo."
        Merge-JsonServer (Join-Path $repo '.mcp.json') $entry
    }
}
function Configure-ClaudeDesktop {
    Step 'Claude Desktop'
    if ($Transport -eq 'http') { Write-Warning 'Claude Desktop uses stdio; registering stdio despite -Transport http.' }
    Merge-JsonServer (Join-Path $env:APPDATA 'Claude/claude_desktop_config.json') $stdioEntry
    Write-Host '    Restart Claude Desktop to pick up the change.'
}
function Configure-Codex {
    Step 'Codex'
    $path = Join-Path $env:USERPROFILE '.codex/config.toml'
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    if ($Transport -eq 'http') { Write-Warning 'Codex MCP is stdio; registering stdio despite -Transport http.' }
    $argsToml = (@($dll) + $commonArgs | ForEach-Object { '"' + ($_ -replace '\\', '\\') + '"' }) -join ', '
    $block = "[mcp_servers.$Name]`ncommand = `"dotnet`"`nargs = [$argsToml]`n"
    $existing = if (Test-Path $path) { Get-Content $path -Raw } else { '' }
    if ($existing -match "(?m)^\[mcp_servers\.$([regex]::Escape($Name))\]") {
        Write-Warning "Codex already has [mcp_servers.$Name]; leaving it untouched. New block:"; Write-Host $block; return
    }
    Backup $path
    $sep = if ($existing.Length -gt 0 -and -not $existing.EndsWith("`n")) { "`n`n" } else { "`n" }
    ($existing + $sep + $block) | Set-Content -Path $path -Encoding UTF8
    Write-Host "    wrote: $path" -ForegroundColor Green
}

switch ($Target) {
    'claude-code' { Configure-ClaudeCode }
    'claude-desktop' { Configure-ClaudeDesktop }
    'codex' { Configure-Codex }
    'all' { Configure-ClaudeCode; Configure-ClaudeDesktop; Configure-Codex }
}
Write-Host ''
Write-Host "Done. Server '$Name' registered ($Transport)." -ForegroundColor Green
if ($Transport -eq 'http') { Write-Host "Start it with: console.bat  (or: `$env:MCP_DOTNET_HTTP_TOKEN='$httpToken'; dotnet `"$dll`" --transport http --port $Port)" -ForegroundColor Yellow }
