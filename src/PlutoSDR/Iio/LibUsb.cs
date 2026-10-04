using System.Runtime.InteropServices;

namespace SDRSharp.PlutoSDR.Iio;

/// <summary>
/// Minimal libusb-1.0 P/Invoke surface. SDR# ships a 32-bit libusb-1.0.dll in its program
/// directory, and that DLL has no unmet dependencies (unlike its libiio.dll, which needs a
/// 32-bit libxml2/libserialport that does not exist on this machine).
/// </summary>
internal static class LibUsb
{
    public const int LIBUSB_SUCCESS = 0;
    public const int LIBUSB_ERROR_TIMEOUT = -7;
    public const int LIBUSB_ERROR_NO_DEVICE = -4;
    public const int LIBUSB_ERROR_PIPE = -9;
    public const int LIBUSB_ERROR_ACCESS = -3;
    public const int LIBUSB_ERROR_BUSY = -6;
    public const int LIBUSB_ERROR_NOT_SUPPORTED = -12;

    public const byte LIBUSB_DT_CONFIG = 0x02;
    public const byte LIBUSB_DT_ENDPOINT = 0x05;
    public const byte LIBUSB_DT_INTERFACE = 0x04;
    public const byte LIBUSB_ENDPOINT_IN = 0x80;

    public const byte LIBUSB_REQUEST_TYPE_VENDOR = 0x40;
    public const byte LIBUSB_RECIPIENT_INTERFACE = 0x01;

    // Pipe management control requests used by the libiio USB backend.
    public const byte IIO_USB_CMD_RESET_PIPES = 0;
    public const byte IIO_USB_CMD_OPEN_PIPE = 1;
    public const byte IIO_USB_CMD_CLOSE_PIPE = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass;
        public byte bDeviceSubClass;
        public byte bDeviceProtocol;
        public byte bMaxPacketSize0;
        public ushort idVendor;
        public ushort idProduct;
        public ushort bcdDevice;
        public byte iManufacturer;
        public byte iProduct;
        public byte iSerialNumber;
        public byte bNumConfigurations;
    }

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_init(out IntPtr ctx);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_exit(IntPtr ctx);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libusb_get_device_list(IntPtr ctx, out IntPtr list);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_free_device_list(IntPtr list, int unrefDevices);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_get_device_descriptor(IntPtr dev, out DeviceDescriptor desc);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_open(IntPtr dev, out IntPtr handle);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libusb_open_device_with_vid_pid(IntPtr ctx, ushort vid, ushort pid);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libusb_get_device(IntPtr handle);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_close(IntPtr handle);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_claim_interface(IntPtr handle, int iface);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_release_interface(IntPtr handle, int iface);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_get_string_descriptor_ascii(IntPtr handle, byte index, byte[] data, int length);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_get_descriptor(IntPtr handle, byte type, byte index, byte[] data, int length);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_control_transfer(IntPtr handle, byte requestType, byte request,
        ushort value, ushort index, byte[] data, ushort length, uint timeout);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_bulk_transfer(IntPtr handle, byte endpoint, byte[] data, int length,
        out int transferred, uint timeout);

    [DllImport("libusb-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libusb_strerror(int code);

    public static string ErrorText(int code)
    {
        try
        {
            IntPtr p = libusb_strerror(code);
            return p == IntPtr.Zero ? code.ToString() : Marshal.PtrToStringAnsi(p) ?? code.ToString();
        }
        catch
        {
            return code.ToString();
        }
    }
}
