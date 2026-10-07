#requires -version 5
<#
.SYNOPSIS
    Build mcp-dotNET and register it with Claude Code, Claude Desktop and/or Codex.

.DESCRIPTION
    Builds the server in Release (unless -NoBuild) and writes the MCP server entry into the
    selected client configuration files, merging into any existing config rather than replacing it.
    A timestamped backup is made before any file is modified.

.PARAMETER Target
    Which client(s) to configure: claude-code, claude-desktop, codex, or all (default).

.PARAMETER ReadOnly
    Register the server with --read-only (disables patch/rename/save tools).

.PARAMETER Name
    MCP server name to register (default 'dotnet').

.PARAMETER NoBuild
    Skip the build; use an existing Release binary.

.EXAMPLE
    ./scripts/install.ps1
.EXAMPLE
    ./scripts/install.ps1 -Target codex -ReadOnly
#>
[CmdletBinding()]
param(
    [ValidateSet('claude-code', 'claude-desktop', 'codex', 'all')]
    [string]$Target = 'all',
    [switch]$ReadOnly,
    [string]$Name = 'dotnet',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/MCPPOC/MCPPOC.csproj'
$args = @()
if ($ReadOnly) { $args += '--read-only' }

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# 1. Build ------------------------------------------------------------------
if (-not $NoBuild) {
    Write-Step 'Building mcp-dotNET (Release)...'
    dotnet build $project -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$dll = Join-Path $repo 'src/MCPPOC/bin/Release/net10.0/mcp-dotnet.dll'
if (-not (Test-Path $dll)) { throw "Server binary not found at $dll. Run without -NoBuild first." }
$dll = (Resolve-Path $dll).Path
Write-Host "    binary: $dll"

# Clients launch the DLL through the shared `dotnet` host, which avoids per-RID exe issues.
$command = 'dotnet'
$commandArgs = @($dll) + $args

function Backup-File($path) {
    if (Test-Path $path) {
        $bak = "$path.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
        Copy-Item $path $bak
        Write-Host "    backup: $bak"
    }
}

function Merge-JsonServer($path) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $root = if (Test-Path $path) { Get-Content $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (-not $root.PSObject.Properties['mcpServers']) {
        $root | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
    }
    $entry = [pscustomobject]@{ command = $command; args = $commandArgs }
    if ($root.mcpServers.PSObject.Properties[$Name]) {
        $root.mcpServers.PSObject.Properties.Remove($Name)
    }
    $root.mcpServers | Add-Member -NotePropertyName $Name -NotePropertyValue $entry
    Backup-File $path
    $root | ConvertTo-Json -Depth 20 | Set-Content -Path $path -Encoding UTF8
    Write-Host "    wrote: $path" -ForegroundColor Green
}

function Configure-ClaudeCode {
    Write-Step 'Claude Code'
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        $cliArgs = @('mcp', 'add', $Name, '--', $command) + $commandArgs
        Write-Host "    running: claude $($cliArgs -join ' ')"
        & claude @cliArgs
        if ($LASTEXITCODE -ne 0) { Write-Warning 'claude mcp add failed; writing user config file instead.'; Merge-JsonServer (Join-Path $env:USERPROFILE '.claude.json') }
    }
    else {
        Write-Warning "'claude' CLI not found; writing project-scoped .mcp.json in the repo instead."
        Merge-JsonServer (Join-Path $repo '.mcp.json')
    }
}

function Configure-ClaudeDesktop {
    Write-Step 'Claude Desktop'
    Merge-JsonServer (Join-Path $env:APPDATA 'Claude/claude_desktop_config.json')
    Write-Host '    Restart Claude Desktop to pick up the change.'
}

function Configure-Codex {
    Write-Step 'Codex'
    $path = Join-Path $env:USERPROFILE '.codex/config.toml'
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $argsToml = ($commandArgs | ForEach-Object { '"' + ($_ -replace '\\', '\\') + '"' }) -join ', '
    $block = @"
[mcp_servers.$Name]
command = "$command"
args = [$argsToml]
"@
    $existing = if (Test-Path $path) { Get-Content $path -Raw } else { '' }
    if ($existing -match "(?m)^\[mcp_servers\.$([regex]::Escape($Name))\]") {
        Write-Warning "Codex already has an [mcp_servers.$Name] block; leaving it untouched. New block below:"
        Write-Host $block
        return
    }
    Backup-File $path
    $sep = if ($existing.Length -gt 0 -and -not $existing.EndsWith("`n")) { "`n`n" } else { "`n" }
    ($existing + $sep + $block + "`n") | Set-Content -Path $path -Encoding UTF8
    Write-Host "    wrote: $path" -ForegroundColor Green
}

switch ($Target) {
    'claude-code' { Configure-ClaudeCode }
    'claude-desktop' { Configure-ClaudeDesktop }
    'codex' { Configure-Codex }
    'all' { Configure-ClaudeCode; Configure-ClaudeDesktop; Configure-Codex }
}

Write-Host ''
Write-Host "Done. Server '$Name' -> $command $($commandArgs -join ' ')" -ForegroundColor Green
