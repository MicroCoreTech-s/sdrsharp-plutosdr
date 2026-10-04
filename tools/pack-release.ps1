<#
.SYNOPSIS
    Builds a distributable zip for a GitHub release.

.DESCRIPTION
    End users only need two files, so a release asset should contain exactly those plus short
    install notes - not the whole source tree. This produces

        dist\SDRSharp.PlutoSDR-<version>.zip
          SDRSharp.PlutoSDR.dll
          MagicLine.txt
          INSTALL.txt

    The host assemblies (SDRSharp.Radio.dll and friends) are deliberately NOT included: SDR# already
    provides them, and a second copy next to the plug-in makes it fail to load.

.PARAMETER Version
    Version string used in the archive name. Defaults to 1.0.0.

.PARAMETER SdrSharpDir
    Only needed when src\PlutoSDR\refs is empty: the folder containing SDRSharp.dotnetN.exe, used to
    extract the compile-time host assemblies.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1 -Version 1.0.0 -SdrSharpDir "C:\SDRSharp"
#>

[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$SdrSharpDir,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

function Fail($message) { Write-Host "error: $message" -ForegroundColor Red; exit 1 }
function Ok($message)   { Write-Host "  [ok]   $message" -ForegroundColor Green }
function Info($message) { Write-Host "  [..]   $message" -ForegroundColor Gray }

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $toolsDir
$pluginDir = Join-Path $repoRoot 'src\PlutoSDR'
$project = Join-Path $pluginDir 'SDRSharp.PlutoSDR.csproj'
$refsDir = Join-Path $pluginDir 'refs'
$distDir = Join-Path $repoRoot 'dist'

Write-Host ''
Write-Host "Packaging SDRSharp.PlutoSDR $Version"
Write-Host '=================================================='

# ---------------------------------------------------------------- host assemblies
$haveRefs = (Test-Path -LiteralPath $refsDir) -and
            (Get-ChildItem -LiteralPath $refsDir -Filter 'SDRSharp.*.dll' -ErrorAction SilentlyContinue).Count -ge 3
if (-not $haveRefs) {
    if (-not $SdrSharpDir) {
        Fail "src\PlutoSDR\refs is empty. Pass -SdrSharpDir <your SDR# folder> so the host assemblies can be extracted."
    }
    Info 'extracting host assemblies'
    & (Join-Path $toolsDir 'extract-refs.ps1') -SdrSharpDir $SdrSharpDir
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) { Fail "extract-refs.ps1 failed ($LASTEXITCODE)" }
}

# ---------------------------------------------------------------- build
Info "building $project ..."
& dotnet build $project -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { Fail "dotnet build returned $LASTEXITCODE" }

$builtDll = Join-Path $pluginDir "bin\$Configuration\SDRSharp.PlutoSDR.dll"
if (-not (Test-Path -LiteralPath $builtDll)) { Fail "build output missing: $builtDll" }

# Guard against ever shipping a host assembly by accident.
$suspicious = Split-Path -Leaf $builtDll
if ($suspicious -ne 'SDRSharp.PlutoSDR.dll') { Fail "unexpected build output name: $suspicious" }
Ok "built $suspicious  ($((Get-Item $builtDll).Length) bytes)"

# ---------------------------------------------------------------- stage
$stage = Join-Path $distDir "SDRSharp.PlutoSDR-$Version"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item -LiteralPath $builtDll -Destination $stage -Force

$magicLine = Join-Path $pluginDir 'MagicLine.txt'
if (-not (Test-Path -LiteralPath $magicLine)) { Fail "MagicLine.txt is missing: $magicLine" }
Copy-Item -LiteralPath $magicLine -Destination $stage -Force

$installNotes = @"
SDR# PlutoSDR frontend plug-in $Version
==================================================

An ADALM-PLUTO receive source for AIRSPY SDR# Studio v1.0.0.1921 (32-bit).

Install
-------
1. Close SDR#.
2. Create a folder for the plug-in inside your SDR# program directory:

       <SDR#>\Plugins\PlutoSDR\

3. Copy SDRSharp.PlutoSDR.dll and MagicLine.txt into that folder.
   Do NOT copy anything else there.

4. Start SDR#, pick "PlutoSDR (ADALM-PLUTO)" from the Source menu, then press Play.

Requirements
------------
* SDR# 1921, 32-bit  (the plug-in must match; SDR# is a 32-bit process)
* The matching 32-bit .NET desktop runtime (8 or 9), normally already present
* The PlutoSDR connected over USB with its RNDIS network adapter up, so that
  192.168.2.1:30431 is reachable

Notes
-----
* The plug-in is passive: it registers the source and does nothing on its own. It never selects a
  source, never starts reception and never writes to SDR#'s configuration.
* On the next start SDR# may land on "Baseband from Sound Card". That is SDR#'s own source-index
  handling for third-party sources - just pick PlutoSDR again.
* Settings live in Plugins\PlutoSDR\PlutoSDR.config; a run log is written to PlutoSDR.log.
* Do not put SDRSharp.Radio.dll, SDRSharp.Common.dll or SDRSharp.PanView.dll next to the plug-in.
  SDR# provides them, and a second copy fails with "Assembly with same name is already loaded".

Full documentation, source and troubleshooting: see the project page.
"@
Set-Content -LiteralPath (Join-Path $stage 'INSTALL.txt') -Value $installNotes -Encoding utf8

# ---------------------------------------------------------------- zip
$zipPath = Join-Path $distDir "SDRSharp.PlutoSDR-$Version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ''
Write-Host 'Archive contents:'
foreach ($entry in (Get-ChildItem -LiteralPath $stage)) {
    Write-Host ("  {0,-28} {1,7} bytes" -f $entry.Name, $entry.Length)
}

Write-Host ''
Write-Host '=================================================='
Ok "release archive: $zipPath  ($([math]::Round((Get-Item $zipPath).Length / 1KB, 1)) KB)"
Write-Host ''
Write-Host 'Attach that zip to a GitHub release tagged v' -NoNewline
Write-Host $Version -ForegroundColor Cyan
Write-Host ''
