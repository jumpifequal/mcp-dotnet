#requires -version 5
<#
.SYNOPSIS
  Builds a ready-to-run distribution of mcp-dotNET into dist/.

.DESCRIPTION
  Produces, under dist/:
    - mcp-dotnet/                a self-contained runnable package (published server binaries +
                                 install/console scripts + docs + LICENSE/NOTICE/CHANGELOG)
    - mcp-dotnet-<ver>.zip       a zip of that package
    - mcp-dotnet.<ver>.nupkg     the NuGet MCP-server / dotnet-tool package
  By default the server is framework-dependent (needs the .NET 10 runtime). Use -SelfContained to
  bundle the runtime for a specific -Runtime (RID), so end users need nothing installed.

.PARAMETER Runtime
  RID for a self-contained build (e.g. win-x64, linux-x64, osx-arm64). Default win-x64.

.PARAMETER SelfContained
  Bundle the .NET runtime (larger, no runtime prerequisite on the target machine).

.PARAMETER NoZip
  Skip creating the .zip.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\pack-dist.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\pack-dist.ps1 -SelfContained -Runtime linux-x64
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [switch]$NoZip
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/MCPPOC/MCPPOC.csproj'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# Version from the single source of truth.
$version = (Select-String -Path (Join-Path $repo 'Directory.Build.props') -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value

$dist = Join-Path $repo 'dist'
$pkgDir = Join-Path $dist 'mcp-dotnet'
if (Test-Path $pkgDir) { Remove-Item -Recurse -Force $pkgDir }
New-Item -ItemType Directory -Force -Path $pkgDir | Out-Null

# 1. Publish the server -----------------------------------------------------
Step "Publishing server $version ($(if ($SelfContained) {"self-contained $Runtime"} else {'framework-dependent'}))..."
$appDir = Join-Path $pkgDir 'app'
$publishArgs = @($project, '-c', 'Release', '-o', $appDir, '--nologo')
if ($SelfContained) {
    $publishArgs += @('-r', $Runtime, '--self-contained', 'true',
        '-p:PublishSingleFile=false', '-p:PackAsTool=false')
} else {
    $publishArgs += @('-p:PackAsTool=false')
}
dotnet publish @publishArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# 2. Collect user-facing files ---------------------------------------------
Step 'Assembling package...'
foreach ($f in 'README.md', 'README.it.md', 'LICENSE', 'NOTICE.md', 'CHANGELOG.md',
    'install.ps1', 'setup.bat', 'console.bat') {
    Copy-Item (Join-Path $repo $f) $pkgDir
}
Copy-Item (Join-Path $repo 'docs') $pkgDir -Recurse
New-Item -ItemType Directory -Force -Path (Join-Path $pkgDir 'scripts') | Out-Null
foreach ($s in 'install.sh', 'console.ps1') {
    Copy-Item (Join-Path $repo "scripts/$s") (Join-Path $pkgDir 'scripts')
}

# A dist-local launcher so the package runs without rebuilding (install.ps1 -NoBuild uses the app dir).
$exeName = if ($SelfContained -and $Runtime -like 'win-*') { 'mcp-dotnet.exe' } else { 'mcp-dotnet.dll' }
$runHint = if ($exeName -like '*.exe') { ".\app\$exeName" } else { "dotnet .\app\$exeName" }
@"
mcp-dotNET $version - distribution package
==========================================

This is a ready-to-run build. The server binaries are in .\app\.

Run the server directly (stdio):
    $runHint

Register it with your AI client (Claude Code / Desktop, Codex):
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoBuild

HTTP transport + maintenance console:
    .\console.bat

Full docs: README.md / docs\. $(if (-not $SelfContained) {"Requires the .NET 10 runtime."} else {"Self-contained ($Runtime): no runtime needed."})
"@ | Set-Content -Path (Join-Path $pkgDir 'RUNME.txt') -Encoding UTF8

# install.ps1 in the package should default to the published app dir, not rebuild.
# (install.ps1 -NoBuild looks under src/.../bin; add a tiny shim path file for clarity.)
"$exeName" | Set-Content -Path (Join-Path $pkgDir 'app/.binary') -Encoding UTF8

# 3. NuGet package ----------------------------------------------------------
Step 'Packing NuGet (dotnet tool / MCP server)...'
dotnet pack $project -c Release -o $dist --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Pack failed.' }

# 4. Zip --------------------------------------------------------------------
if (-not $NoZip) {
    Step 'Zipping...'
    $zip = Join-Path $dist "mcp-dotnet-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $pkgDir -DestinationPath $zip
    Write-Host "    $zip"
}

Write-Host ''
Write-Host "Distribution ready in $dist" -ForegroundColor Green
Get-ChildItem $dist | Where-Object { -not $_.PSIsContainer -or $_.Name -eq 'mcp-dotnet' } |
    ForEach-Object { Write-Host ("    {0}" -f $_.Name) }
