using System.Runtime.InteropServices;

namespace SDRSharp.PlutoSDR.Iio;

/// <summary>
/// IIOD over the Pluto's USB "IIO" interface.
///
/// libiio's USB backend does not use vendor control requests for the data path: it locates the
/// interface whose string descriptor is "IIO", claims it, and then runs the ordinary IIOD text
/// protocol over the interface's bulk endpoint pair. Only pipe management goes through control
/// transfers. This class reproduces exactly that.
/// </summary>
internal sealed class UsbTransport : IIioTransport
{
    private const ushort PlutoVid = 0x0456;
    private static readonly ushort[] PlutoPids = { 0xB673, 0xB674 };

    private const uint ControlTimeoutMs = 3000;
    private const uint BulkTimeoutMs = 5000;

    private IntPtr _ctx = IntPtr.Zero;
    private IntPtr _handle = IntPtr.Zero;
    private int _interface = -1;
    private byte _epIn;
    private byte _epOut;
    private bool _claimed;
    private readonly string _description;
    private readonly object _ioLock = new object();
    private bool _disposed;

    public string Description => _description;

    public UsbTransport(int preferBus = -1, int preferAddress = -1)
    {
        int ret = LibUsb.libusb_init(out _ctx);
        if (ret != LibUsb.LIBUSB_SUCCESS)
            throw new IioException($"libusb_init failed ({ret}): {LibUsb.ErrorText(ret)}");

        // Deliberately avoid libusb_get_device_list(): the libusb 1.0.21 build that ships with SDR#
        // mis-handles the Pluto's composite device. It logs
        //   error [init_device] device '\\.\USB#VID_0456&PID_B673&MI_02#...' is no longer connected!
        // for a sub-interface that has no driver bound, which truncates the returned device list and
        // can fault the process. Opening straight from the VID/PID is stable and is all we need.
        foreach (ushort pid in PlutoPids)
        {
            _handle = LibUsb.libusb_open_device_with_vid_pid(_ctx, PlutoVid, pid);
            if (_handle != IntPtr.Zero) break;
        }

        if (_handle == IntPtr.Zero)
        {
            Cleanup();
            throw new IioException(
                "PlutoSDR not found on USB (looking for 0456:b673). Check the cable, and that the " +
                "IIO interface is bound to the WinUSB driver.");
        }

        int bus = -1, address = -1;
        try
        {
            IntPtr dev = LibUsb.libusb_get_device(_handle);
            if (dev != IntPtr.Zero)
            {
                bus = BusNumber(dev);
                address = DeviceAddress(dev);
            }
        }
        catch { }

        try
        {
            DiscoverInterface();
        }
        catch
        {
            Cleanup();
            throw;
        }

        int claimRet = LibUsb.libusb_claim_interface(_handle, _interface);
        if (claimRet != LibUsb.LIBUSB_SUCCESS)
        {
            Cleanup();
            throw new IioException(
                $"Unable to claim the IIO interface {_interface} ({claimRet}: {LibUsb.ErrorText(claimRet)}). " +
                "Is another program (iio_info, IIO Oscilloscope, SDR++) using the Pluto?");
        }
        _claimed = true;

        // Reset and open the pipe the same way libiio does before any protocol traffic.
        if (PipeCommand(LibUsb.IIO_USB_CMD_RESET_PIPES, 0) != LibUsb.LIBUSB_SUCCESS)
        {
            Cleanup();
            throw new IioException("IIO_RESET_PIPES control request failed.");
        }
        if (PipeCommand(LibUsb.IIO_USB_CMD_OPEN_PIPE, 0) != LibUsb.LIBUSB_SUCCESS)
        {
            Cleanup();
            throw new IioException("IIO_OPEN_PIPE control request failed.");
        }

        _description = $"USB {bus}.{address} if{_interface} ep 0x{_epIn:x2}/0x{_epOut:x2}";
    }

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_get_bus_number")]
    private static extern byte libusb_get_bus_number(IntPtr dev);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_get_device_address")]
    private static extern byte libusb_get_device_address(IntPtr dev);

    private static int BusNumber(IntPtr dev) => libusb_get_bus_number(dev);
    private static int DeviceAddress(IntPtr dev) => libusb_get_device_address(dev);

    /// <summary>
    /// Walks the configuration descriptor looking for the interface named "IIO". The descriptor is
    /// fetched with a plain GET_DESCRIPTOR control transfer rather than libusb_get_descriptor,
    /// because several Windows libusb builds (including the one SDR# ships) do not export that
    /// entry point, and because the raw bytes are self-describing and free of struct-layout
    /// assumptions.
    /// </summary>
    private void DiscoverInterface()
    {
        var buf = new byte[1024];
        int len = LibUsb.libusb_control_transfer(_handle,
            0x80,       // device-to-host, standard, device
            0x06,       // GET_DESCRIPTOR
            0x0200,     // configuration descriptor, index 0
            0,
            buf, (ushort)buf.Length, ControlTimeoutMs);

        if (len < 9 || buf[1] != LibUsb.LIBUSB_DT_CONFIG)
            throw new IioException($"Unable to read the USB configuration descriptor ({len}).");

        int total = buf[2] | (buf[3] << 8);
        if (total > len) total = len;

        int fallbackIface = -1, fallbackIn = -1, fallbackOut = -1;
        int pos = buf[0];
        int curIface = -1, curIn = -1, curOut = -1, curClass = -1;

        void Commit()
        {
            if (curIface < 0) return;
            if (fallbackIface < 0 && curClass == 0xFF && curIn >= 0 && curOut >= 0)
            {
                fallbackIface = curIface;
                fallbackIn = curIn;
                fallbackOut = curOut;
            }
        }

        while (pos + 2 <= total)
        {
            int dlen = buf[pos];
            int dtype = buf[pos + 1];
            if (dlen <= 0 || pos + dlen > total) break;

            if (dtype == LibUsb.LIBUSB_DT_INTERFACE)
            {
                Commit();
                curIface = buf[pos + 2];
                curClass = buf[pos + 5];
                curIn = -1;
                curOut = -1;

                byte iInterface = buf[pos + 8];
                if (iInterface != 0 && ReadString(iInterface) == "IIO")
                {
                    int epos = pos + dlen;
                    int inEp = -1, outEp = -1;
                    while (epos + 2 <= total && buf[epos + 1] == LibUsb.LIBUSB_DT_ENDPOINT)
                    {
                        byte addr = buf[epos + 2];
                        if ((addr & LibUsb.LIBUSB_ENDPOINT_IN) != 0) { if (inEp < 0) inEp = addr; }
                        else if (outEp < 0) outEp = addr;
                        epos += buf[epos];
                    }
                    if (inEp >= 0 && outEp >= 0)
                    {
                        _interface = curIface;
                        _epIn = (byte)inEp;
                        _epOut = (byte)outEp;
                        return;
                    }
                }
            }
            else if (dtype == LibUsb.LIBUSB_DT_ENDPOINT && curIface >= 0)
            {
                byte addr = buf[pos + 2];
                if ((addr & LibUsb.LIBUSB_ENDPOINT_IN) != 0) { if (curIn < 0) curIn = addr; }
                else if (curOut < 0) curOut = addr;
            }

            pos += dlen;
        }
        Commit();

        if (fallbackIface >= 0)
        {
            // Some firmware builds omit the "IIO" string: fall back to the vendor-specific
            // interface that carries a bulk endpoint pair, which is what libiio uses anyway.
            _interface = fallbackIface;
            _epIn = (byte)fallbackIn;
            _epOut = (byte)fallbackOut;
            return;
        }

        throw new IioException(
            $"No IIO interface found (interfaces seen: {DescribeInterfaces(buf, total)}).");
    }

    private static string DescribeInterfaces(byte[] buf, int total)
    {
        var parts = new List<string>();
        int pos = buf[0];
        while (pos + 2 <= total)
        {
            int dlen = buf[pos];
            if (dlen <= 0) break;
            if (buf[pos + 1] == LibUsb.LIBUSB_DT_INTERFACE)
                parts.Add($"if{buf[pos + 2]}(class=0x{buf[pos + 5]:x2} eps={buf[pos + 4]})");
            pos += dlen;
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private string ReadString(byte index)
    {
        try
        {
            var sb = new byte[128];
            int ret = LibUsb.libusb_get_string_descriptor_ascii(_handle, index, sb, sb.Length);
            if (ret <= 0) return null;
            return System.Text.Encoding.ASCII.GetString(sb, 0, ret);
        }
        catch
        {
            return null;
        }
    }

    private int PipeCommand(byte request, ushort value)
    {
        return LibUsb.libusb_control_transfer(_handle,
            (byte)(LibUsb.LIBUSB_REQUEST_TYPE_VENDOR | LibUsb.LIBUSB_RECIPIENT_INTERFACE),
            request, value, (ushort)_interface, null, 0, ControlTimeoutMs);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_handle == IntPtr.Zero) throw new IioException("USB transport is closed.");
        byte[] tmp = offset == 0 ? buffer : new byte[count];
        int transferred;
        int ret = LibUsb.libusb_bulk_transfer(_handle, _epIn, tmp, count, out transferred, BulkTimeoutMs);
        if (ret == LibUsb.LIBUSB_ERROR_TIMEOUT) return 0;
        if (ret != LibUsb.LIBUSB_SUCCESS)
            throw new IioException($"USB bulk read failed ({ret}): {LibUsb.ErrorText(ret)}");
        if (offset != 0 && transferred > 0) Buffer.BlockCopy(tmp, 0, buffer, offset, transferred);
        return transferred;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (_handle == IntPtr.Zero) throw new IioException("USB transport is closed.");
        byte[] tmp = offset == 0 ? buffer : new byte[count];
        if (offset != 0) Buffer.BlockCopy(buffer, offset, tmp, 0, count);

        int sent = 0;
        while (sent < count)
        {
            int remaining = count - sent;
            byte[] chunk = tmp;
            int chunkOffset = sent;
            if (chunkOffset != 0)
            {
                chunk = new byte[remaining];
                Buffer.BlockCopy(tmp, chunkOffset, chunk, 0, remaining);
            }
            int transferred;
            int ret = LibUsb.libusb_bulk_transfer(_handle, _epOut, chunk, remaining, out transferred, BulkTimeoutMs);
            if (ret != LibUsb.LIBUSB_SUCCESS)
                throw new IioException($"USB bulk write failed ({ret}): {LibUsb.ErrorText(ret)}");
            if (transferred <= 0) throw new IioException("USB bulk write stalled.");
            sent += transferred;
        }
        lock (_ioLock) { }
    }

    public void Flush() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }

    private void Cleanup()
    {
        if (_handle != IntPtr.Zero)
        {
            try
            {
                PipeCommand(LibUsb.IIO_USB_CMD_RESET_PIPES, 0);
                if (_claimed) LibUsb.libusb_release_interface(_handle, _interface);
            }
            catch { }
            LibUsb.libusb_close(_handle);
            _handle = IntPtr.Zero;
            _claimed = false;
        }
        if (_ctx != IntPtr.Zero)
        {
            LibUsb.libusb_exit(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
