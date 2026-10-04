# Deployment guide — SDR# 1921 PlutoSDR frontend

This turns an ADALM-PLUTO into a first-class SDR# receive source: it appears in the **Source** menu,
streams IQ at full rate, and brings its own source panel for tuning, sample rate, RF bandwidth,
gain/AGC and live RSSI.

The plug-in is **passive**: once it has registered the source it does nothing on its own. It never
selects a source, never starts reception and never writes to SDR#'s configuration.

> 中文版见 [DEPLOY.zh-CN.md](DEPLOY.zh-CN.md)。

| Route | When to use it | Source needed | .NET SDK needed |
|---|---|---|---|
| [A. Copy the release](#route-a-copy-the-release-simplest) | you have a prebuilt DLL | no | no |
| [B. One-step script](#route-b-one-step-script-recommended) | you have the repository | yes | yes |
| [C. Build from scratch](#route-c-build-from-scratch) | you want to modify the code | yes | yes |

---

## 1. Requirements

| | Requirement | How to check |
|---|---|---|
| SDR# | **1921, 32-bit** (`SDRSharp.dotnet8.exe` / `SDRSharp.dotnet9.exe`) | see below |
| .NET | the matching **32-bit** desktop runtime (8 or 9) | `dir "C:\Program Files (x86)\dotnet\shared\Microsoft.WindowsDesktop.App"` |
| PlutoSDR | connected over USB, **RNDIS adapter up**, `192.168.2.1:30431` reachable | `Test-NetConnection 192.168.2.1 -Port 30431` |
| Permissions | write access to the SDR# folder | the deploy script writes there and will fail loudly |

> **Why 32-bit?** SDR# 1921 itself is an x86 process, so the plug-in must be x86 too or it will not
> load at all.

Confirm the Pluto is reachable:

```powershell
Test-NetConnection 192.168.2.1 -Port 30431 -InformationLevel Quiet
```

`True` means you are good. If it is `False`, see [section 6](#6-troubleshooting), item 3.

---

## Route A: Copy the release (simplest)

Two files are all it takes — no source tree, no SDK:

```
<SDR# folder>\
  SDRSharp.dotnet9.exe
  ...
  Plugins\                         <- create it if missing
    PlutoSDR\                      <- any folder name
      SDRSharp.PlutoSDR.dll
      MagicLine.txt
```

**Step 1** create the folder:

```powershell
New-Item -ItemType Directory -Force "C:\SDRSharp\Plugins\PlutoSDR"
```

**Step 2** copy both files (replace `<release>` with the unpacked release folder):

```powershell
Copy-Item "<release>\SDRSharp.PlutoSDR.dll" "C:\SDRSharp\Plugins\PlutoSDR\" -Force
Copy-Item "<release>\MagicLine.txt"          "C:\SDRSharp\Plugins\PlutoSDR\" -Force
```

`MagicLine.txt` is how SDR# discovers a plug-in and must contain exactly one line:

```xml
<add key="PlutoSDR" value="SDRSharp.PlutoSDR.PlutoSDRPlugin,SDRSharp.PlutoSDR" />
```

The format is `fully.qualified.TypeName,AssemblyName`. A typo makes SDR# silently ignore the plug-in.

**Step 3** make sure no host assembly was copied along. `Plugins\PlutoSDR\` must contain exactly one
`SDRSharp.*.dll`, and it must be `SDRSharp.PlutoSDR.dll`:

```powershell
Get-ChildItem "C:\SDRSharp\Plugins\PlutoSDR" -Filter "SDRSharp.*.dll"
```

If `SDRSharp.Radio.dll`, `SDRSharp.Common.dll` or `SDRSharp.PanView.dll` show up, **delete them**.
SDR# provides those, and a second copy fails with
`Assembly with same name is already loaded`.

---

## Route B: One-step script (recommended)

The script validates the SDR# folder and its bitness, extracts the host assemblies if needed, builds
the plug-in, copies it into `Plugins\PlutoSDR\`, removes stray host assemblies, and checks that the
Pluto is reachable.

```powershell
cd <repository root>
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -SdrSharpDir "C:\SDRSharp"
```

> A `#` in the path (for example `D:\SDR#`) is legal, but the path **must be quoted** or PowerShell
> treats `#` as the start of a comment.

Install only, without rebuilding:

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -SdrSharpDir "C:\SDRSharp" -SkipBuild
```

Measured output:

```
1. Checking the SDR# installation
  [ok]   SDRSharp.dotnet9.exe  (32-bit)
  [ok]   .NET 9 x86 desktop runtime  (8.0.28, 9.0.12)
2. Preparing the plug-in assembly
  [..]   src\PlutoSDR\refs is empty; extracting host assemblies from the local SDR# install
  [ok]   SDRSharp.Common.dll  (28 KB)
  [ok]   SDRSharp.PanView.dll  (324 KB)
  [ok]   SDRSharp.Radio.dll  (328 KB)
  [ok]   assembly ready: SDRSharp.PlutoSDR.dll  (58880 bytes)
3. Installing into SDR#
  [ok]   copied SDRSharp.PlutoSDR.dll -> C:\SDRSharp\Plugins\PlutoSDR
  [ok]   copied MagicLine.txt (how SDR# discovers the plug-in)
4. Checking that the Pluto is reachable (default TCP transport)
  [ok]   192.168.2.1:30431 reachable
Installed.
```

---

## Route C: Build from scratch

**Step 1** install the .NET SDK (9.0 or later).

**Step 2** extract the host reference assemblies. The plug-in compiles against three SDR# assemblies
that ship *inside* `SDRSharp.dotnetN.exe`:

```powershell
powershell -ExecutionPolicy Bypass -File tools\extract-refs.ps1 -SdrSharpDir "C:\SDRSharp"
```

The script writes them to `src\PlutoSDR\refs\` and **verifies each one by reading its own assembly
metadata**, rather than trusting the name/offset pairing in the executable's embedded manifest
(which is easy to get wrong). All three should report `[ok]`.

These assemblies are Analog Devices property — **do not commit them**. `.gitignore` already excludes
that folder.

**Step 3** build:

```powershell
dotnet build src\PlutoSDR\SDRSharp.PlutoSDR.csproj -c Release
```

Output: `src\PlutoSDR\bin\Release\SDRSharp.PlutoSDR.dll`

**Step 4** copy it into SDR# as in [Route A](#route-a-copy-the-release-simplest).

> The project targets `net9.0-windows` and `x86`. The `refs\` references are marked `Private=false`
> so the host assemblies are never copied into the output directory.

---

## 4. First run and verification

1. Start `SDRSharp.dotnet9.exe`.
2. **Source** menu → **PlutoSDR (ADALM-PLUTO)**.
3. Press **Play** (▶).

You should see, in order:

- Title bar: `AIRSPY SDR# Studio v1.0.0.1921 32-Bit - PlutoSDR (ADALM-PLUTO)`
- **Source** panel: `Analog Devices PlutoSDR Rev.B (Z7010-AD9364)`, firmware, serial, libiio version,
  transport, and the status `Connected.`
- Status line refreshing with `RSSI xx.x dB  gain xx.x dB  streaming`
- Live spectrum and waterfall

### Confirm from the log (recommended)

SDR# swallows exceptions raised while opening a source, so **the log is the only place** where you
can see what the frontend actually did:

```powershell
Get-Content "C:\SDRSharp\Plugins\PlutoSDR\PlutoSDR.log" -Tail 20
```

Files in `Plugins\PlutoSDR\`:

| File | Purpose |
|---|---|
| `PlutoSDR.log` | registration, resulting Source menu layout, transport, device configuration, stream start, periodic block counts |
| `PlutoSDR.config` | plug-in settings (device URI, sample rate, bandwidth, gain …) |

**A healthy log looks like this** (measured):

```
=== plug-in Initialize ===
host control object = SDRSharp.SharpControlProxy, main form = SDRSharp.MainForm
frontend registered as 'PlutoSDR (ADALM-PLUTO)' (built-in sources: 11)
Source menu now: [0] 'Configure' tag=<null> | ... | [13] 'PlutoSDR (ADALM-PLUTO)' tag=11 | [14] 'Baseband from Sound Card' tag=<null>
```

Those four lines mean **the plug-in is ready** — and that is where it stops on its own; it will not
select a source for you. After you pick PlutoSDR and press Play, these follow:

```
opened Analog Devices PlutoSDR Rev.B (Z7010-AD9364) fw v0.39-dirty via TCP 192.168.2.1:30431; rate=2000000 LO=100000000 gain=40dB
streaming started: buffer 61440 samples (245760 bytes per block), rate 2000000
first block delivered to SDR#: 61440 IQ samples at 2000000 SPS
streaming: 400 blocks delivered (61440 samples each)
```

Acceptance points:

| Log line | Meaning |
|---|---|
| `frontend registered` | registration succeeded |
| `[13] 'PlutoSDR (ADALM-PLUTO)' tag=11` | the Source menu slot is correct |
| `opened ... via TCP` | the real hardware answered |
| `first block delivered` | IQ is genuinely flowing into SDR#'s receive chain |

`PluginError.log` should be empty or unrelated to this plug-in.

---

## 5. Configuration

### Source panel (inside the Source panel)

| Control | Notes |
|---|---|
| Device URI | `auto` (default), `ip:<host>`, or `usb` |
| Sample rate | 0.5 … 30.72 MSPS, free text entry |
| RF bandwidth | 0.2 … 56 MHz |
| AGC (slow attack) | switches `gain_control_mode` |
| RX gain | manual gain, −1 … 73 dB |
| Status line | model, firmware, serial, libiio version, transport; RSSI and gain while streaming |

### Plug-in panel (SDR# **Display** menu → plug-ins)

- **Use PlutoSDR as the source now** — switches immediately, same as clicking the Source menu entry
- **Re-register the frontend** — retry if registration failed
- The top shows registration status and the host's current source

### Settings file

`Plugins\PlutoSDR\PlutoSDR.config`, plain `key=value`:

```ini
# SDR# PlutoSDR plugin settings
DeviceUri=auto
SampleRate=2000000
Bandwidth=2000000
GainDb=40
Agc=false
Frequency=432948310
```

Restart SDR# after editing. The plug-in writes this back when SDR# **closes normally**;
force-killing the process skips saving.

### Choosing the device URI

| Value | Route | Notes |
|---|---|---|
| `auto` | TCP `192.168.2.1` → `pluto.local` | **recommended**, full rate, no native dependencies |
| `ip:192.168.2.1` | explicit TCP address | use it if the Pluto's subnet was changed |
| `ip:pluto.local` | mDNS | survives an IP change |
| `usb` | libusb bulk endpoints | see the warning below — **do not use by default** |

> ⚠️ **`usb` currently crashes SDR#.** The 32-bit `libusb-1.0.dll` (1.0.21) that SDR# ships raises an
> access violation while enumerating this composite device (`0xC0000005`); libusb's own log says
> `device '\\.\USB#VID_0456&PID_B673&MI_02#...' is no longer connected!`. The 64-bit libusb 1.0.26
> used by libiio has no such problem. To enable the USB path, replace `libusb-1.0.dll` in the SDR#
> folder with a newer **32-bit** build first.

---

## 6. Troubleshooting

### 1. No “PlutoSDR (ADALM-PLUTO)” entry in the Source menu

```powershell
# a) are both files there?
Get-ChildItem "C:\SDRSharp\Plugins\PlutoSDR"

# b) is MagicLine.txt exactly one line with the right type name?
Get-Content "C:\SDRSharp\Plugins\PlutoSDR\MagicLine.txt"

# c) is the DLL 32-bit? (expect 0x014C)
$b=[IO.File]::ReadAllBytes("C:\SDRSharp\Plugins\PlutoSDR\SDRSharp.PlutoSDR.dll")
$pe=[BitConverter]::ToInt32($b,0x3C); '{0:X4}' -f [BitConverter]::ToUInt16($b,$pe+4)

# d) what did the host report?
Get-Content "C:\SDRSharp\PluginError.log" -Tail 30
```

Usual causes: a typo in `MagicLine.txt`, a non-x86 DLL, or host assemblies sitting next to the
plug-in.

### 2. Entry present, but no signal / the Source panel stays disconnected

Read `PlutoSDR.log`:

- `frontend registered` present but no `opened ...` → the connection failed; the next line will be
  `Open('...') failed: ...`. Go to item 3.
- No `=== plug-in Initialize ===` at all → the plug-in was never loaded. Back to item 1.
- `opened` present but no `streaming started` → the source is open but reception has not begun.
  Press **Play**.

### 3. `Could not open the PlutoSDR` / connection timeout

The default route is TCP, so check the network side first:

```powershell
Test-NetConnection 192.168.2.1 -Port 30431
Get-NetAdapter | Where-Object { $_.InterfaceDescription -match 'RNDIS|Remote NDIS' }
```

- No RNDIS adapter → the Pluto's USB driver is not installed properly. Check Device Manager for
  unknown devices and reinstall the Pluto Windows driver package if needed.
- Adapter present but unreachable → subnet conflict. The Pluto defaults to `192.168.2.1`; if another
  adapter on the machine uses the same subnet they will collide. Change the Pluto's IP or use
  `ip:<new address>`.
- Cross-check with the vendor tools:

```powershell
& "C:\Program Files\IIO Oscilloscope\bin\iio_info.exe" -s
```

Listing both a `usb:...` and an `ip:192.168.2.1` context means the hardware and driver are fine.

### 4. SDR# dies instantly (`0xC0000005`)

Almost always `DeviceUri` is set to `usb`. Set it back in `Plugins\PlutoSDR\PlutoSDR.config`:

```ini
DeviceUri=auto
```

### 5. Next start lands on "Baseband from Sound Card"

This is **SDR#'s own** behaviour, not a plug-in fault. SDR# persists the selected source as an index
and reloads it with `selected = (stored > count) ? count : stored`. A third-party source sits exactly
at index `count`, which survives that clamp, and at the moment the check runs the plug-in has not
registered yet — so `SourceIsSoundCard` (`selected >= count`) reports true.

Just pick **PlutoSDR (ADALM-PLUTO)** from the Source menu again. The plug-in deliberately does **not**
rewrite `iqSource` in SDR#'s configuration to work around this.

### 6. Blank spectrum / no audio

- **Play** was not pressed (▶).
- Frequency outside the Pluto's range. The AD9364 covers **70 MHz – 6 GHz**; outside that the write
  is clamped or fails.
- Gain too low: set RX gain around 40 dB, or enable AGC.
- Tune to a known signal (local FM broadcast, 88–108 MHz) to prove the chain.

### 7. RSSI pinned above 90 dB, spectrum washed out

Input saturated — usually **no antenna connected** or too much front-end gain. Lower the RX gain or
enable AGC.

### 8. Sample rate will not go higher / stuttering

- USB 2.0 realistically tops out near **6 MSPS**; above that samples are dropped. 2–5 MSPS is the
  comfortable range.
- Higher rates cost proportionally more CPU, especially with several DSP plug-ins enabled.

### 9. Proving the plug-in really runs at full rate

The block counters in the log give you the throughput directly:

```
streaming: 400 blocks delivered (61440 samples each)    12:12:53.852
streaming: 800 blocks delivered (61440 samples each)    12:13:06.141
```

400 × 61440 samples ÷ 12.257 s ≈ **2.005 MSPS** — matching the configured rate means zero loss.

Or use the standalone diagnostic, which needs no SDR#:

```powershell
dotnet run --project tools\PlutoIioTest -c Release -- "C:\SDRSharp" ip:192.168.2.1 40 200
```

It reads the hardware's real sample-rate registers back (`HARDWARE sampling_frequency`,
`rx_path_rates`) and reports the effective sample rate, so you can tell a genuine 2 MSPS from a link
quietly dropping three quarters of the samples.

---

## 7. Updating and uninstalling

### Update

Re-running the deploy script overwrites the installed plug-in; `PlutoSDR.config` is left alone, so
your settings survive. **SDR# must be closed first** or the DLL is locked — the script checks for a
running instance and refuses with a clear message instead of failing halfway through:

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -SdrSharpDir "C:\SDRSharp" -SkipBuild
```

It reports which of the three cases happened:

```
  [ok]   installed SDRSharp.PlutoSDR.dll (60928 bytes)              # first install
  [ok]   re-installed SDRSharp.PlutoSDR.dll (60928 bytes, unchanged) # same build
  [ok]   upgraded SDRSharp.PlutoSDR.dll: 58880 -> 60928 bytes        # replaced an older build
```

### Uninstall

Delete the plug-in folder; SDR# is left with no other changes:

```powershell
Remove-Item -Recurse -Force "C:\SDRSharp\Plugins\PlutoSDR"
```

---

## 8. Acceptance checklist

- [ ] `Plugins\PlutoSDR\` contains both `SDRSharp.PlutoSDR.dll` and `MagicLine.txt`
- [ ] No other `SDRSharp.*.dll` sits in that folder
- [ ] After starting SDR#, the **Source** menu contains `PlutoSDR (ADALM-PLUTO)`
- [ ] Selecting it makes the title bar read `- PlutoSDR (ADALM-PLUTO)`
- [ ] The Source panel shows the correct model / firmware / serial and the status `Connected.`
- [ ] After pressing Play the status line shows `streaming` and the waterfall scrolls
- [ ] `PlutoSDR.log` contains `frontend registered`, `opened ... via TCP` and
      `first block delivered to SDR#`
- [ ] `PluginError.log` has no entry related to this plug-in
