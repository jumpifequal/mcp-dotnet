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
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/MCPPOC/MCPPOC.csproj'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# 1. Build ------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK not found. Install .NET 10: winget install --id Microsoft.DotNet.SDK.Preview'
}
# A distribution package ships the published binary under .\app\ and no source tree.
$distDll = Join-Path $repo 'app/mcp-dotnet.dll'
$repoDll = Join-Path $repo 'bin/MCPPOC/Release/net10.0/mcp-dotnet.dll'
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

# Warn if Claude Desktop is currently running: it keeps its own in-memory copy of
# claude_desktop_config.json and periodically overwrites the file with that copy, so an edit made to
# the file while the app stays open can be silently undone the next time the app saves. Quitting the
# app fully (tray icon > Quit, not just closing the window) before running this script, then
# restarting it afterwards, avoids that.
if (Get-Process -Name 'Claude' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*AnthropicClaude*' }) {
    Write-Warning "Claude Desktop is currently running. It may overwrite claude_desktop_config.json with its own in-memory copy after this script writes it, undoing the change. Fully quit Claude Desktop (tray icon > Quit), run this script, then start Claude Desktop again."
}

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

# PowerShell's own ConvertTo-Json inserts a doubled space after every colon and an indentation that
# compounds with key-name length instead of a fixed per-level width, which bloats and disfigures any
# file it touches (observed: a 9.7 KB Claude Desktop config became 38 KB of jagged indentation). This
# writes plain, standard 2-space-indented JSON instead, so merging a server entry does not visibly
# wreck the rest of the file. Also sidesteps a PowerShell 'switch' gotcha: 'switch ($value) {...}'
# iterates per-ELEMENT when $value is an array, silently collapsing multi-item arrays to their first
# element -- this uses explicit if/elseif instead, which never does that.
function Format-JsonString([string]$Value) {
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.Append('"')
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq '"') { [void]$sb.Append('\"') }
        elseif ($ch -eq '\') { [void]$sb.Append('\\') }
        elseif ($ch -eq "`n") { [void]$sb.Append('\n') }
        elseif ($ch -eq "`r") { [void]$sb.Append('\r') }
        elseif ($ch -eq "`t") { [void]$sb.Append('\t') }
        elseif ([int][char]$ch -lt 0x20) { [void]$sb.Append([string]::Format('\u{0:x4}', [int][char]$ch)) }
        else { [void]$sb.Append($ch) }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function ConvertTo-CleanJson {
    param($InputObject, [int]$Depth = 0)
    $pad = '  ' * $Depth
    $pad1 = '  ' * ($Depth + 1)

    if ($null -eq $InputObject) { return 'null' }
    if ($InputObject -is [bool]) { return $(if ($InputObject) { 'true' } else { 'false' }) }
    if ($InputObject -is [string]) { return Format-JsonString $InputObject }
    if ($InputObject -is [int] -or $InputObject -is [long] -or $InputObject -is [double] -or $InputObject -is [decimal]) { return "$InputObject" }

    # Arrays before the object branch: a [string] is itself IEnumerable (per-char) and is already
    # handled above; dictionaries/PSCustomObjects are excluded here and handled as objects below.
    if ($InputObject -is [array] -or ($InputObject -is [System.Collections.IEnumerable] -and
            $InputObject -isnot [System.Collections.IDictionary] -and $InputObject -isnot [System.Management.Automation.PSCustomObject])) {
        $items = @($InputObject)
        if ($items.Count -eq 0) { return '[]' }
        $lines = foreach ($it in $items) { "$pad1" + (ConvertTo-CleanJson -InputObject $it -Depth ($Depth + 1)) }
        return "[`n" + ($lines -join ",`n") + "`n$pad]"
    }

    if ($InputObject -is [System.Management.Automation.PSCustomObject] -or $InputObject -is [System.Collections.IDictionary]) {
        $names = if ($InputObject -is [System.Collections.IDictionary]) { $InputObject.Keys } else { $InputObject.PSObject.Properties.Name }
        $names = @($names)
        # Windows PowerShell 5.1's ConvertFrom-Json represents a JSON '{}' it reads back as a single
        # property whose NAME IS $null (not an empty string) -- that is an internal artifact, not real
        # data, so both a truly empty property list and this one-null-named-property shape mean '{}'.
        if ($names.Count -eq 0 -or ($names.Count -eq 1 -and $null -eq $names[0])) { return '{}' }
        $lines = foreach ($propName in $names) {
            $val = $InputObject.$propName
            "$pad1" + (Format-JsonString $propName) + ': ' + (ConvertTo-CleanJson -InputObject $val -Depth ($Depth + 1))
        }
        return "{`n" + ($lines -join ",`n") + "`n$pad}"
    }

    return Format-JsonString "$InputObject"
}

# Windows PowerShell 5.1's 'Set-Content -Encoding UTF8' writes a UTF-8 byte-order mark, which several
# JSON/TOML parsers (including Claude Desktop's) reject or silently ignore, making an edit look like
# it never took effect. Write-Utf8NoBom writes the exact bytes requested, with no BOM, every time.
function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function Merge-JsonServer($path, [object]$entry) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $root = if (Test-Path $path) { (Get-Content $path -Raw) | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (-not $root.PSObject.Properties['mcpServers']) { $root | Add-Member mcpServers ([pscustomobject]@{}) }
    if ($root.mcpServers.PSObject.Properties[$Name]) { $root.mcpServers.PSObject.Properties.Remove($Name) }
    $root.mcpServers | Add-Member -NotePropertyName $Name -NotePropertyValue $entry
    Backup $path
    Write-Utf8NoBom -Path $path -Content (ConvertTo-CleanJson -InputObject $root)
    Write-Host "    wrote: $path" -ForegroundColor Green
}

# Escapes a path for a TOML basic string: a single backslash becomes two (TOML basic strings use the
# same backslash-escaping as JSON). The -replace PATTERN '\\' is a *regex* matching one literal
# backslash; the REPLACEMENT '\\' is taken as a literal 2-character string by .NET's replacement engine
# (which only treats '$' specially, never '\'), so this doubles every backslash exactly once. Writing
# '\\\\' here (quadruple) would double an already-escaped path a second time, corrupting it.
function ConvertTo-TomlString([string]$Value) {
    '"' + ($Value -replace '\\', '\\') + '"'
}

# stdio entry: launch the DLL through the shared dotnet host.
$stdioEntry = [pscustomobject]@{ command = 'dotnet'; args = @($dll) + $commonArgs }
# http entry: URL + bearer header (client connects to an already-running server).
$httpEntry = [pscustomobject]@{ type = 'http'; url = "http://127.0.0.1:$Port/mcp"; headers = [pscustomobject]@{ Authorization = "Bearer $httpToken" } }
$entry = if ($Transport -eq 'http') { $httpEntry } else { $stdioEntry }

# Finds the real headless Claude Code CLI, never the Claude Desktop GUI app. On a machine with the
# combined "Claude" desktop app installed, a bare 'claude' on PATH commonly resolves to
# '...\AnthropicClaude\claude.exe' -- the GUI launcher, not a CLI. Invoking THAT with 'mcp add ...'
# does not register anything: the GUI process just sees a recognizable file-path argument (the .dll)
# and pops its own "Open '<file>' in Claude?" attach-file confirmation dialog, while silently doing
# nothing for MCP registration. The real CLI, where present, instead lives under a versioned
# '...\Claude\claude-code\<version>\<hash>\claude.exe' path that is usually NOT on PATH at all.
function Find-ClaudeCodeCli {
    $candidates = Get-ChildItem -Path (Join-Path $env:APPDATA 'Claude/claude-code/*/*/claude.exe') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
    if ($candidates) { return $candidates[0].FullName }

    $onPath = Get-Command claude -ErrorAction SilentlyContinue
    if ($onPath -and $onPath.Source -notlike '*AnthropicClaude*') { return $onPath.Source }

    return $null
}

function Configure-ClaudeCode {
    Step 'Claude Code'
    $cli = Find-ClaudeCodeCli
    if ($cli) {
        if ($Transport -eq 'http') {
            & $cli mcp add $Name --transport http "http://127.0.0.1:$Port/mcp" --header "Authorization: Bearer $httpToken"
        } else {
            & $cli mcp add $Name -- dotnet $dll @commonArgs
        }
        if ($LASTEXITCODE -ne 0) { Write-Warning 'claude mcp add failed; writing .mcp.json instead.'; Merge-JsonServer (Join-Path $repo '.mcp.json') $entry }
    } else {
        Write-Warning "Claude Code CLI not found (only the Claude Desktop app binary is on PATH, which cannot register MCP servers via the command line); writing project-scoped .mcp.json in the repo."
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
    $argsToml = (@($dll) + $commonArgs | ForEach-Object { ConvertTo-TomlString $_ }) -join ', '
    $block = "[mcp_servers.$Name]`ncommand = `"dotnet`"`nargs = [$argsToml]`n"
    $existing = if (Test-Path $path) { Get-Content $path -Raw } else { '' }
    if ($existing -match "(?m)^\[mcp_servers\.$([regex]::Escape($Name))\]") {
        Write-Warning "Codex already has [mcp_servers.$Name]; leaving it untouched. New block:"; Write-Host $block; return
    }
    Backup $path
    $sep = if ($existing.Length -gt 0 -and -not $existing.EndsWith("`n")) { "`n`n" } else { "`n" }
    Write-Utf8NoBom -Path $path -Content ($existing + $sep + $block)
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
