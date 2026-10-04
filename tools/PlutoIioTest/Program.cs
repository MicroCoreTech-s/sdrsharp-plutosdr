using System.Diagnostics;
using System.Runtime.InteropServices;
using SDRSharp.PlutoSDR;

// Standalone check of the IIOD client + Pluto device layer used by the SDR# plugin.
// Usage: PlutoIioTest [sdrSharpDir] [uri] [gainDb] [blocks] [bufferSamples] [blockBytes]

internal static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // The folder holding libusb-1.0.dll (normally the SDR# program directory). It only matters
        // for the USB transport; the default TCP transport needs no native libraries at all.
        string sdrDir = args.Length > 0 ? args[0] : AppContext.BaseDirectory;
        string uri = args.Length > 1 ? args[1] : "auto";

        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, asm, path) =>
        {
            if (name.StartsWith("libusb", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(sdrDir, name);
                if (File.Exists(candidate)) return NativeLibrary.Load(candidate);
            }
            return IntPtr.Zero;
        });

        Console.WriteLine($"[i] SDR# dir: {sdrDir}   uri: {uri}");

        if (Environment.GetEnvironmentVariable("PLUTO_TRACE") == "1")
        {
            try
            {
                using var t = new SDRSharp.PlutoSDR.Iio.UsbTransport();
                Console.WriteLine("[trace] UsbTransport ok: " + t.Description);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[trace] UsbTransport threw:\n" + ex);
            }
        }

        var device = new PlutoDevice();
        var sw = Stopwatch.StartNew();
        try
        {
            device.Open(uri);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[X] Open failed: " + ex.Message);
            return 2;
        }
        Console.WriteLine($"[+] Connected in {sw.ElapsedMilliseconds} ms via {device.TransportDescription}");
        Console.WriteLine($"    model       = {device.Model}");
        Console.WriteLine($"    firmware    = {device.FirmwareVersion}");
        Console.WriteLine($"    serial      = {device.Serial}");
        Console.WriteLine($"    libiio      = {device.LibiioVersion}");
        Console.WriteLine($"    freq range  = {device.MinFrequency} .. {device.MaxFrequency} Hz");
        Console.WriteLine($"    rate range  = {device.MinSampleRate} .. {device.MaxSampleRate} Hz");
        Console.WriteLine($"    bw   range  = {device.MinBandwidth} .. {device.MaxBandwidth} Hz");

        // ---- configure ----
        try
        {
            device.SetSampleRate(2_000_000);
            device.SetBandwidth(2_000_000);
            device.SetFrequency(100_000_000);
            int gainArg = args.Length > 2 && int.TryParse(args[2], out int g) ? g : 40;
            device.SetGain(gainArg, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[X] Configuration failed: " + ex.Message);
            device.Dispose();
            return 3;
        }
        Console.WriteLine($"[+] Configured: rate={device.SampleRate} Hz  LO={device.Frequency} Hz  " +
                          $"gain={device.GainDb} dB mode={device.AgcMode}");
        Console.WriteLine($"    readback: rssi={device.ReadRssi():0.0} dB  hwGain={device.ReadHardwareGain():0.0} dB");
        Console.WriteLine($"    HARDWARE sampling_frequency = {device.ReadBackSampleRate()}");
        Console.WriteLine($"    HARDWARE rf_bandwidth       = {device.ReadBackBandwidth()}");
        Console.WriteLine($"    HARDWARE RX_LO frequency    = {device.ReadBackLoFrequency()}");
        Console.WriteLine($"    HARDWARE rx_path_rates      = {device.ReadBackPathRates()}");

        // ---- stream ----
        int bufferSamples = args.Length > 4 && int.TryParse(args[4], out int bs) ? bs : 0;
        if (bufferSamples > 0) device.BufferSamples = bufferSamples; else device.ConfigureBuffering();
        int blockBytes = args.Length > 5 && int.TryParse(args[5], out int bb) ? bb : device.BufferBytes;
        bufferSamples = device.BufferSamples;
        Console.WriteLine($"    kernel buffer = {bufferSamples} samples, read block = {blockBytes} bytes");
        try
        {
            device.StartStreaming();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[X] StartStreaming failed: " + ex.Message);
            device.Dispose();
            return 4;
        }

        var buffer = new byte[blockBytes];
        int totalSamples = 0;
        long minVal = long.MaxValue, maxVal = long.MinValue;
        double sumSquares = 0;
        long nonZero = 0, notMultipleOf16 = 0;
        var totalWatch = Stopwatch.StartNew();

        int blockCount = args.Length > 3 && int.TryParse(args[3], out int bc) ? bc : 12;

        for (int block = 0; block < blockCount; block++)
        {
            int got;
            try
            {
                got = device.ReadBlock(buffer, blockBytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[X] ReadBlock failed on block {block}: {ex.Message}");
                break;
            }
            if (got <= 0)
            {
                Console.WriteLine($"[!] block {block}: no data");
                continue;
            }

            int samples = got / 4;
            for (int i = 0, o = 0; i < samples; i++, o += 4)
            {
                short si = (short)(buffer[o] | (buffer[o + 1] << 8));
                short sq = (short)(buffer[o + 2] | (buffer[o + 3] << 8));
                if (si < minVal) minVal = si;
                if (sq < minVal) minVal = sq;
                if (si > maxVal) maxVal = si;
                if (sq > maxVal) maxVal = sq;
                sumSquares += (double)si * si + (double)sq * sq;
                if (si != 0 || sq != 0) nonZero++;
                if ((si & 0xF) != 0) notMultipleOf16++;
                if ((sq & 0xF) != 0) notMultipleOf16++;
            }
            totalSamples += samples;
            if (blockCount <= 12 || block % 40 == 0)
                Console.WriteLine($"    block {block,4}: {got,6} bytes -> {samples,6} IQ samples");
        }
        totalWatch.Stop();
        device.StopStreaming();

        Console.WriteLine($"    protocol: transport reads={device.TransportReadCalls}  READBUF chunks={device.TotalChunks}  " +
                          $"last block chunks={device.LastChunkCount} max chunk={device.LastChunkMax} bytes");

        Console.WriteLine();
        Console.WriteLine($"[+] Captured {totalSamples} IQ samples in {totalWatch.ElapsedMilliseconds} ms " +
                          $"({totalSamples / Math.Max(1, totalWatch.ElapsedMilliseconds) / 1000.0:0.00} MSPS effective)");
        if (totalSamples > 0)
        {
            double rms = Math.Sqrt(sumSquares / (totalSamples * 2.0));
            double lowNibbleFraction = notMultipleOf16 * 100.0 / (totalSamples * 2.0);
            Console.WriteLine($"    value range = {minVal} .. {maxVal}   rms={rms:0.0}");
            Console.WriteLine($"    non-zero    = {nonZero * 100.0 / totalSamples:0.0}%");
            Console.WriteLine($"    values with a non-zero low nibble = {lowNibbleFraction:0.0}%");
            Console.WriteLine(lowNibbleFraction < 5.0
                ? "    => samples are LEFT-JUSTIFIED 12-bit (value << 4 in a 16-bit container)."
                : "    => samples are right-justified 16-bit values.");
            Console.WriteLine(minVal > -32000 && maxVal < 32000 && nonZero * 100.0 / totalSamples > 50
                ? "    => live IQ data (not silence)."
                : "    => WARNING: data may be railed or empty.");
        }

        device.Dispose();
        Console.WriteLine("[+] done");
        return totalSamples > 0 ? 0 : 5;
    }
}


