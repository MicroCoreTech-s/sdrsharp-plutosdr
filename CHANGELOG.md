# Changelog

All notable changes to this project are documented here.
This project adheres to [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-04

First release. Verified against real hardware: *Analog Devices PlutoSDR Rev.B
(Z7010-AD9364), firmware v0.39-dirty*, sustaining **2.005 MSPS** with no dropped
samples inside SDR# 1921.

### Added

- ADALM-PLUTO frontend for SDR# 1921 (`SDRSharp.PlutoSDR.PlutoSDRIO`) implementing
  `IFrontendController`, `IIQStreamController`, `ITunableSource`,
  `ISampleRateChangeSource`, `ISpectrumProvider`, `IControlAwareObject` and
  `IConfigurationPanelProvider`.
- Source panel with device URI, sample rate, RF bandwidth, gain/AGC and live RSSI.
- `ISharpPlugin` entry point that registers the frontend with SDR# through the
  host's own `MainForm.LoadSourceType` method.
- IIOD protocol client (`VERSION`, `PRINT`, `READ`, `WRITE`, `OPEN`, `CLOSE`,
  `READBUF`) with two transports:
  - IIOD over TCP to the Pluto's USB-Ethernet interface (default, no native
    dependencies),
  - IIOD over the USB `"IIO"` interface via libusb bulk transfers (opt-in).
- Context-XML driven discovery of `ad9361-phy` / `cf-ad9361-lpc`, the RX LO
  channel, the gain channel and the ordered scan channels forming the buffer mask.
- `tools/extract-refs.ps1` to unpack the SDR# host assemblies from
  `SDRSharp.dotnetN.exe`.
- `tools/deploy.ps1` one-step build-and-install.
- `tools/PlutoIioTest` standalone diagnostic for the device layer, usable without
  SDR#.

### Notes

- The plug-in is passive by design: it never selects a source, never starts
  reception and never writes to SDR#'s own configuration.
- `usb` transport is not the default. The 32-bit `libusb-1.0.dll` (1.0.21) that
  SDR# ships raises an access violation while enumerating this composite device;
  the 64-bit libusb 1.0.26 used by libiio does not. See README.
- After the PlutoSDR source has been used once, SDR# may start on
  *"Baseband from Sound Card"* the next time. This is SDR#'s own source-index
  clamping, described in README → Known limitations.
