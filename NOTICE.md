# Notice

## Trademarks

This project is not affiliated with, endorsed by, or sponsored by Analog Devices, Inc. or Airspy.

"ADALM-PLUTO", "PlutoSDR", "SDR#", "SDRSharp" and "Airspy" are trademarks of their respective
owners. They are used here only to describe what this software interoperates with.

## SDR# host assemblies are not distributed here

The plug-in is compiled against three assemblies that ship inside the SDR# executable rather than as
loose files:

* `SDRSharp.Radio.dll`
* `SDRSharp.Common.dll`
* `SDRSharp.PanView.dll`

Those assemblies are the property of Analog Devices, are **not** part of this repository, and are
excluded by `.gitignore`. Every user extracts them from their own copy of SDR# with:

```
powershell -ExecutionPolicy Bypass -File tools\extract-refs.ps1 -SdrSharpDir "<your SDR# folder>"
```

The only thing taken from SDR# at run time is the interface contract the host expects from a
frontend, which the plug-in implements; the host assemblies themselves are never redistributed.

## Native libraries

The plug-in uses no bundled native libraries for its default transport. It speaks the IIOD protocol
directly over TCP to the PlutoSDR's own network interface.

The optional USB transport calls into whatever 32-bit `libusb-1.0.dll` is present in the SDR#
program directory; that file belongs to the SDR# distribution and is not redistributed here either.

## Screenshots

The screenshots in `docs/images/` are from a real session. The Windows desktop chrome has been
cropped out and the PlutoSDR device serial number has been removed.
