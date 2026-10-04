<#
.SYNOPSIS
    Extracts the SDR# host assemblies this plug-in compiles against.

.DESCRIPTION
    SDRSharp.Radio.dll, SDRSharp.Common.dll and SDRSharp.PanView.dll ship inside the SDR#
    executable rather than as loose files, so a fresh clone of this repository cannot compile until
    they are unpacked from a local SDR# installation.

    This script finds them and writes them to src\PlutoSDR\refs\. The assemblies are Analog Devices'
    property and are therefore NOT part of this repository - every user extracts them from their own
    copy of SDR#. The refs folder is git-ignored.

    How it works: inside SDRSharp.dotnetN.exe the managed assemblies are stored as plain,
    uncompressed PE images, and a manifest near the end of the file lists each one with its offset
    and size in 256-byte units. Candidate blobs are collected from that manifest (taking the
    numbers both before and after each listed name, since the exact pairing is an implementation
    detail), and every candidate is then identified by reading its OWN assembly metadata. Nothing is
    trusted from the manifest except the offsets, so the result verifies itself.

.PARAMETER SdrSharpDir
    The folder containing SDRSharp.dotnet9.exe (or SDRSharp.dotnet8.exe).

.PARAMETER OutputDir
    Where to write the assemblies. Defaults to <repo>\src\PlutoSDR\refs.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File extract-refs.ps1 -SdrSharpDir "C:\SDRSharp"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SdrSharpDir,

    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

function Fail($message) { Write-Host "error: $message" -ForegroundColor Red; exit 1 }
function Ok($message)   { Write-Host "  [ok]   $message" -ForegroundColor Green }
function Info($message) { Write-Host "  [..]   $message" -ForegroundColor Gray }

# Little-endian int64 built from 8 Latin-1 characters of a .NET string.
function ReadInt64([string]$text, [int]$start) {
    [long]$value = 0
    for ($k = 7; $k -ge 0; $k--) {
        $value = ($value -shl 8) -bor [int][char]$text[$start + $k]
    }
    return $value
}

$targets = @('SDRSharp.Radio', 'SDRSharp.Common', 'SDRSharp.PanView')

if (-not $OutputDir) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
    $OutputDir = Join-Path $repoRoot 'src\PlutoSDR\refs'
}

Write-Host ''
Write-Host 'Extracting SDR# host assemblies'
Write-Host '=================================================='

if (-not (Test-Path -LiteralPath $SdrSharpDir)) { Fail "directory not found: $SdrSharpDir" }
$SdrSharpDir = (Resolve-Path -LiteralPath $SdrSharpDir).Path

$exe = Get-ChildItem -LiteralPath $SdrSharpDir -Filter 'SDRSharp*.exe' -ErrorAction SilentlyContinue |
       Where-Object { $_.Name -match '^SDRSharp\.(dotnet\d+\.exe|exe)$' } |
       Sort-Object -Property @{ Expression = { if ($_.Name -match 'dotnet(\d+)') { [int]$Matches[1] } else { 0 } } } -Descending |
       Select-Object -First 1
if (-not $exe) { Fail "no SDRSharp*.exe found in $SdrSharpDir" }
Ok "host executable: $($exe.Name)  ($([math]::Round($exe.Length / 1MB, 1)) MB)"

$bytes = [System.IO.File]::ReadAllBytes($exe.FullName)
$fileLength = $bytes.Length

# Latin-1 maps every byte to exactly one char, so string positions equal byte offsets.
$latin = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)

# ---------------------------------------------------------------- collect candidate blobs
Info 'scanning the embedded-file manifest ...'

$offsets = New-Object 'System.Collections.Generic.HashSet[long]'
$rx = [regex]'[\x01-\x0F][\x04-\x5A]([\x20-\x7E]{4,90})\x00(.{16})'

foreach ($m in $rx.Matches($latin)) {
    $name = $m.Groups[1].Value
    if ($name -notmatch '\.(dll|exe|json)$') { continue }

    $tail = $m.Groups[2].Value
    $pairs = New-Object System.Collections.Generic.List[object]
    $pairs.Add(@((ReadInt64 $tail 0), (ReadInt64 $tail 8)))          # numbers after the name
    if ($m.Index -ge 16) {                                           # numbers before the name
        $pairs.Add(@((ReadInt64 $latin ($m.Index - 16)), (ReadInt64 $latin ($m.Index - 8))))
    }

    foreach ($pair in $pairs) {
        $offset = $pair[0] * 256
        $size = $pair[1] * 256
        if ($offset -le 0 -or $size -le 0) { continue }
        if ($offset -ge $fileLength -or $size -gt $fileLength) { continue }
        if ($offset + $size -gt $fileLength) { continue }
        [void]$offsets.Add($offset)
    }
}

if ($offsets.Count -eq 0) { Fail 'no embedded files found - is this really an SDR# executable?' }
Ok "$($offsets.Count) candidate blobs"

# ---------------------------------------------------------------- identify each blob by its metadata
Info 'identifying assemblies by their own metadata ...'

$sorted = $offsets | Sort-Object
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("sdrsharp-refs-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

$found = @{}
try {
    for ($index = 0; $index -lt $sorted.Count; $index++) {
        $offset = $sorted[$index]
        # The blobs are contiguous: a blob runs until the next one starts.
        $end = if ($index + 1 -lt $sorted.Count) { $sorted[$index + 1] } else { $fileLength }
        $length = $end - $offset
        if ($length -le 512) { continue }
        if ($bytes[$offset] -ne 0x4D -or $bytes[$offset + 1] -ne 0x5A) { continue }   # 'MZ'

        $tempFile = Join-Path $tempDir 'blob.dll'
        $stream = [System.IO.File]::Create($tempFile)
        try {
            $stream.Write($bytes, [int]$offset, [int]$length)
        } finally {
            $stream.Dispose()
        }

        try {
            # Reads the assembly identity from metadata without loading the assembly, so 32-bit
            # images are fine here even in a 64-bit shell.
            $identity = [System.Reflection.AssemblyName]::GetAssemblyName($tempFile).Name
        } catch {
            continue
        }

        if ($targets -notcontains $identity) { continue }
        if ($found.ContainsKey($identity)) { continue }

        if (-not (Test-Path -LiteralPath $OutputDir)) {
            New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
        }
        $destination = Join-Path $OutputDir "$identity.dll"
        Copy-Item -LiteralPath $tempFile -Destination $destination -Force
        $found[$identity] = $destination
        Ok "$identity.dll  ($([math]::Round($length / 1KB)) KB)"
    }
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- report
Write-Host ''
Write-Host '=================================================='
$missing = $targets | Where-Object { -not $found.ContainsKey($_) }
if ($missing.Count -gt 0) {
    Write-Host "missing: $($missing -join ', ')" -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'The plug-in needs all three. If one is missing, this SDR# build may store it' -ForegroundColor Yellow
    Write-Host 'differently; copy that file manually into the output folder.' -ForegroundColor Yellow
    exit 2
}

Write-Host 'Done. Host assemblies written to:' -ForegroundColor Green
Write-Host "  $OutputDir"
Write-Host ''
Write-Host 'These files are Analog Devices property and must NOT be committed - .gitignore already' -ForegroundColor Cyan
Write-Host 'excludes that folder.'
Write-Host ''
