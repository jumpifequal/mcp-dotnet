#requires -version 5
<#
.SYNOPSIS
  Flattens bin/ after a Release build: copies the server's and GUI console's final DLL/EXE and runtime
  dependencies straight into bin/ (one level, no subfolders), then removes the now-empty per-project
  build subfolders, so bin/ holds only what a recipient would get in an official distribution: what is
  needed to install and run mcp-dotNET on another machine. Safe to run repeatedly, and cross-platform
  (pwsh on Linux/macOS, Windows PowerShell or pwsh on Windows); does nothing if a project hasn't been
  built yet. Never touches tests/ or the per-project bin/obj under it.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ./Scripts/flatten-bin.ps1
.EXAMPLE
  pwsh ./Scripts/flatten-bin.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $repo 'bin'

# Project name -> its TargetFramework folder name, so this script does not have to guess or glob
# across every TFM that happens to exist (it also skips a project cleanly if never built).
$projects = @(
    @{ Name = 'MCPPOC'; Tfm = 'net10.0' }
    @{ Name = 'MCPPOC.Console'; Tfm = 'net10.0-windows' }
)

$flattened = 0
foreach ($proj in $projects) {
    # Built via Join-Path (not a "name\config\tfm" string) so this works with either path separator.
    $src = Join-Path (Join-Path (Join-Path $bin $proj.Name) $Configuration) $proj.Tfm
    if (-not (Test-Path $src)) { continue }
    Get-ChildItem -Path $src -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $bin $_.Name) -Force
        $flattened++
    }
    # Sub-folders a publish/runtime might add (satellite resource assemblies, native runtimes, etc.)
    # are intentionally left under the per-project folder below rather than flattened too -- only the
    # directly usable DLL/EXE belong at the bin/ top level.
}

# Remove the now-empty per-project subfolders so bin/ is genuinely flat.
foreach ($proj in $projects) {
    $projDir = Join-Path $bin $proj.Name
    if (Test-Path $projDir) { Remove-Item -Recurse -Force $projDir }
}

# WPF's XAML markup compiler builds an auxiliary "<Project>_<hash>_wpftmp" helper project; with
# BaseOutputPath set per project name, its own differently-named output lands directly under bin/
# (a sibling of MCPPOC.Console/, not nested inside it) and is never used at runtime. Safe to remove.
Get-ChildItem -Path $bin -Directory -Filter '*_wpftmp' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -Recurse -Force $_.FullName }

Write-Host "Flattened $flattened file(s) into $bin" -ForegroundColor Green
