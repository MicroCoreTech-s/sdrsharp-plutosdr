<#
.SYNOPSIS
    Builds and installs the PlutoSDR frontend plug-in into an SDR# 1921 installation.

.DESCRIPTION
    Copies SDRSharp.PlutoSDR.dll and MagicLine.txt into the plug-in folder SDR# scans
    (Plugins\PlutoSDR next to SDRSharp.dotnetN.exe), which is how SDR# discovers plug-ins.
    Nothing else is needed at run time: the default transport talks IIOD over TCP to the Pluto's
    own USB-Ethernet interface and uses no native libraries at all.

    Host assemblies (SDRSharp.Radio.dll, SDRSharp.Common.dll, SDRSharp.PanView.dll) are deliberately
    NOT copied into the plug-in folder - SDR# provides them, and a second copy collides with the
    already-loaded assembly.

    If src\PlutoSDR\refs is empty the script runs tools\extract-refs.ps1 first, so a fresh clone can
    be built in one command.

.PARAMETER SdrSharpDir
    The folder containing SDRSharp.dotnet9.exe (or SDRSharp.dotnet8.exe).

.PARAMETER SkipBuild
    Install the existing build output instead of rebuilding.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -SdrSharpDir "C:\SDRSharp"

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -SdrSharpDir "C:\SDRSharp" -SkipBuild
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SdrSharpDir,

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$SkipBuild
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

Write-Host ''
Write-Host 'SDR# PlutoSDR plug-in - build and install'
Write-Host '=================================================='

# ---------------------------------------------------------------- 1. validate target
Write-Host ''
Write-Host '1. Checking the SDR# installation'

if (-not (Test-Path -LiteralPath $SdrSharpDir)) { Fail "directory not found: $SdrSharpDir" }
$SdrSharpDir = (Resolve-Path -LiteralPath $SdrSharpDir).Path

$exe = Get-ChildItem -LiteralPath $SdrSharpDir -Filter 'SDRSharp*.exe' -ErrorAction SilentlyContinue |
       Where-Object { $_.Name -match '^SDRSharp\.(dotnet\d+\.exe|exe)$' } |
       Sort-Object -Property @{ Expression = { if ($_.Name -match 'dotnet(\d+)') { [int]$Matches[1] } else { 0 } } } -Descending |
       Select-Object -First 1
if (-not $exe) {
    Fail "no SDRSharp*.exe in $SdrSharpDir - point this at the folder holding SDRSharp.dotnet9.exe"
}

# SDR# 1921 is a 32-bit process; the plug-in must match.
$bytes = [System.IO.File]::ReadAllBytes($exe.FullName)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
if ($machine -ne 0x014C) {
    Fail ("$($exe.Name) is not a 32-bit executable (machine=0x{0:X4}); this plug-in only targets 32-bit SDR#." -f $machine)
}
Ok "$($exe.Name)  (32-bit)"

$targetMajor = if ($exe.Name -match 'dotnet(\d+)') { $Matches[1] } else { '9' }
$runtimeRoot = 'C:\Program Files (x86)\dotnet\shared\Microsoft.WindowsDesktop.App'
if (Test-Path $runtimeRoot) {
    $have = Get-ChildItem $runtimeRoot | Select-Object -ExpandProperty Name
    if ($have -match "^$targetMajor\.") {
        Ok ".NET $targetMajor x86 desktop runtime  ($($have -join ', '))"
    } else {
        Write-Host "  [warn] no 32-bit .NET $targetMajor desktop runtime found (have: $($have -join ', '))" -ForegroundColor Yellow
    }
} else {
    Write-Host "  [warn] 32-bit .NET runtime folder not found: $runtimeRoot" -ForegroundColor Yellow
}

# The plug-in DLL is loaded and locked while the host runs, so an upgrade would fail halfway
# through with a bare "file in use". Detect it up front and say what to do.
$running = @(Get-Process -Name 'SDRSharp*' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ((Split-Path -Parent $_.Path).TrimEnd('\') -ieq $SdrSharpDir.TrimEnd('\')) }
    catch { $false }
})
if ($running.Count -gt 0) {
    Write-Host ''
    Write-Host "  [stop] SDR# is running from this folder (PID $($running.Id -join ', '))." -ForegroundColor Yellow
    Fail 'close SDR# and run this script again - the plug-in DLL cannot be replaced while it is loaded.'
}

# ---------------------------------------------------------------- 2. host assemblies
Write-Host ''
Write-Host '2. Preparing the plug-in assembly'

$haveRefs = (Test-Path -LiteralPath $refsDir) -and
            (Get-ChildItem -LiteralPath $refsDir -Filter 'SDRSharp.*.dll' -ErrorAction SilentlyContinue).Count -ge 3

if (-not $SkipBuild -and -not $haveRefs) {
    Info 'src\PlutoSDR\refs is empty; extracting host assemblies from the local SDR# install'
    & (Join-Path $toolsDir 'extract-refs.ps1') -SdrSharpDir $SdrSharpDir
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) { Fail "extract-refs.ps1 failed ($LASTEXITCODE)" }
    $haveRefs = (Test-Path -LiteralPath $refsDir) -and
                (Get-ChildItem -LiteralPath $refsDir -Filter 'SDRSharp.*.dll' -ErrorAction SilentlyContinue).Count -ge 3
    if (-not $haveRefs) { Fail 'host assemblies could not be extracted' }
}

$builtDll = Join-Path $pluginDir "bin\$Configuration\SDRSharp.PlutoSDR.dll"

if ($SkipBuild) {
    if (-not (Test-Path -LiteralPath $builtDll)) { Fail "-SkipBuild was used but $builtDll does not exist" }
    Info 'skipping the build, using the existing output'
} elseif (-not (Test-Path -LiteralPath $project)) {
    if (Test-Path -LiteralPath $builtDll) {
        Info 'no project file, using the existing build output'
    } else {
        Fail "neither $project nor a prebuilt $builtDll exists"
    }
} else {
    Info "building $project ..."
    & dotnet build $project -c $Configuration -v q --nologo
    if ($LASTEXITCODE -ne 0) { Fail "build failed (dotnet build returned $LASTEXITCODE)" }
}
if (-not (Test-Path -LiteralPath $builtDll)) { Fail "build output missing: $builtDll" }
Ok "assembly ready: $(Split-Path -Leaf $builtDll)  ($((Get-Item $builtDll).Length) bytes)"

# ---------------------------------------------------------------- 3. install
Write-Host ''
Write-Host '3. Installing into SDR#'

$pluginsRoot = Join-Path $SdrSharpDir 'Plugins'
$target = Join-Path $pluginsRoot 'PlutoSDR'
if (-not (Test-Path -LiteralPath $pluginsRoot)) { New-Item -ItemType Directory -Path $pluginsRoot -Force | Out-Null }
if (-not (Test-Path -LiteralPath $target))      { New-Item -ItemType Directory -Path $target -Force      | Out-Null }

# Report the upgrade rather than just "copied", so it is obvious whether an existing install
# was actually replaced. The size is read into a plain integer BEFORE the copy: FileInfo caches
# its stat data on first access, so asking an object captured earlier would return the new size.
$installedDll = Join-Path $target 'SDRSharp.PlutoSDR.dll'
$previousSize = if (Test-Path -LiteralPath $installedDll) { (Get-Item -LiteralPath $installedDll).Length } else { -1 }

Copy-Item -LiteralPath $builtDll -Destination $target -Force

$currentSize = (Get-Item -LiteralPath $installedDll).Length
if ($previousSize -lt 0) {
    Ok "installed SDRSharp.PlutoSDR.dll ($currentSize bytes)"
} elseif ($previousSize -eq $currentSize) {
    Ok "re-installed SDRSharp.PlutoSDR.dll ($currentSize bytes, unchanged)"
} else {
    Ok "upgraded SDRSharp.PlutoSDR.dll: $previousSize -> $currentSize bytes"
}

$magicLine = Join-Path $pluginDir 'MagicLine.txt'
if (-not (Test-Path -LiteralPath $magicLine)) { Fail "MagicLine.txt is missing: $magicLine" }
Copy-Item -LiteralPath $magicLine -Destination $target -Force
Ok 'copied MagicLine.txt (how SDR# discovers the plug-in)'

# A stray host assembly next to the plug-in breaks loading with
# "Assembly with same name is already loaded".
$strays = Get-ChildItem -LiteralPath $target -Filter 'SDRSharp.*.dll' -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -ne 'SDRSharp.PlutoSDR.dll' }
foreach ($stray in $strays) {
    Remove-Item -LiteralPath $stray.FullName -Force
    Write-Host "  [clean] removed host assembly that must not be here: $($stray.Name)" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 4. reachability
Write-Host ''
Write-Host '4. Checking that the Pluto is reachable (default TCP transport)'

$probe = Join-Path $toolsDir 'PlutoIioTest\bin\Release\net9.0-windows\PlutoIioTest.exe'
if (Test-Path -LiteralPath $probe) {
    Info 'running the transport diagnostic ...'
    & $probe $SdrSharpDir 'ip:192.168.2.1' 3 2>&1 |
        Where-Object { $_ -match 'Connected|model|rate=|Captured|HARDWARE sampling|^\[X\]' } |
        ForEach-Object { "       $_" }
} else {
    $tcp = Test-NetConnection -ComputerName 192.168.2.1 -Port 30431 -InformationLevel Quiet -WarningAction SilentlyContinue
    if ($tcp) { Ok '192.168.2.1:30431 reachable' } else {
        Write-Host '  [warn] cannot reach 192.168.2.1:30431. Check that the Pluto is plugged in and its RNDIS network adapter is up.' -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------- done
Write-Host ''
Write-Host '==================================================' -ForegroundColor Cyan
Write-Host 'Installed.' -ForegroundColor Green
Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
Write-Host "  1. Start $($exe.Name)"
Write-Host '  2. Source menu -> PlutoSDR (ADALM-PLUTO)'
Write-Host '  3. Press Play'
Write-Host ''
Write-Host 'Logs:' -ForegroundColor Cyan
Write-Host "  $(Join-Path $target 'PlutoSDR.log')"
Write-Host "  $(Join-Path $SdrSharpDir 'PluginError.log')"
Write-Host ''
