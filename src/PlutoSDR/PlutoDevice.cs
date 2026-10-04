using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SDRSharp.PlutoSDR.Iio;

namespace SDRSharp.PlutoSDR;

/// <summary>
/// Owns one IIO context on a PlutoSDR: discovers the RX device/channels from the context XML,
/// exposes tuning/gain/sample-rate control, and pulls raw IQ blocks from the hardware buffer.
/// </summary>
internal sealed class PlutoDevice : IDisposable
{
    private const string PhyDeviceName = "ad9361-phy";
    private const string RxDeviceName = "cf-ad9361-lpc";

    private IiodClient _iio;
    private string _phyId;
    private string _rxId;
    private string _loChannel;
    private string _gainChannel;
    private string[] _scanChannels = Array.Empty<string>();
    private uint[] _mask = { 0x3 };
    private int _bytesPerSample = 4;

    private bool _opened;
    private bool _streaming;

    public string TransportDescription { get; private set; } = "(not connected)";
    public string Model { get; private set; } = "PlutoSDR";
    public string FirmwareVersion { get; private set; } = "?";
    public string Serial { get; private set; } = "?";
    public string LibiioVersion { get; private set; } = "?";

    public long MinFrequency { get; private set; } = 70_000_000;
    public long MaxFrequency { get; private set; } = 6_000_000_000;
    public long MinSampleRate { get; private set; } = 520_833;
    public long MaxSampleRate { get; private set; } = 30_720_000;
    public long MinBandwidth { get; private set; } = 200_000;
    public long MaxBandwidth { get; private set; } = 56_000_000;

    public double SampleRate { get; set; } = 2_000_000;
    public long Frequency { get; set; } = 100_000_000;
    public int GainDb { get; set; } = 40;
    public long Bandwidth { get; set; } = 2_000_000;
    public bool AgcEnabled { get; set; }
    public string AgcMode { get; private set; } = "manual";

    public bool IsOpen => _opened;
    public bool IsStreaming => _streaming;

    /// <summary>Opens the device. <paramref name="uri"/> is "auto", "usb" or "ip:host".</summary>
    public void Open(string uri)
    {
        if (_opened) return;

        IIioTransport transport = null;
        var errors = new List<string>();

        foreach (string candidate in ExpandUri(uri))
        {
            try
            {
                transport = CreateTransport(candidate);
                break;
            }
            catch (Exception ex)
            {
                errors.Add($"{candidate}: {ex.Message}");
            }
        }

        if (transport == null)
            throw new IioException("Could not open the PlutoSDR.\n" + string.Join("\n", errors));

        _iio = new IiodClient(transport);
        try
        {
            LibiioVersion = _iio.GetVersion();
            string xml = _iio.GetXml();
            Discover(xml);
            ApplyAll();
            _opened = true;
            TransportDescription = transport.Description;
        }
        catch
        {
            _iio?.Dispose();
            _iio = null;
            throw;
        }
    }

    private static IIioTransport CreateTransport(string uri)
    {
        if (uri.StartsWith("ip:", StringComparison.OrdinalIgnoreCase))
            return new TcpTransport(uri.Substring(3).Trim());

        if (uri.StartsWith("usb", StringComparison.OrdinalIgnoreCase))
        {
            int bus = -1, addr = -1;
            string rest = uri.Substring(3).TrimStart(':');
            if (!string.IsNullOrEmpty(rest))
            {
                var parts = rest.Split('.');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], out bus) && int.TryParse(parts[1], out addr))
                { /* pinned to a specific bus/address */ }
                else { bus = -1; addr = -1; }
            }
            return new UsbTransport(bus, addr);
        }

        throw new IioException("Unsupported IIO URI: " + uri);
    }

    private static IEnumerable<string> ExpandUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // The Pluto's USB-Ethernet (RNDIS) interface exposes the very same IIOD service on
            // 192.168.2.1:30431, and it sustains the full sample rate. It is preferred over the USB
            // backend because the 32-bit libusb 1.0.21 that SDR# ships faults with an access
            // violation while enumerating this composite device (verified; the 64-bit libusb 1.0.26
            // used by libiio has no such problem). Select "usb" explicitly to try that path.
            yield return "ip:192.168.2.1";
            yield return "ip:pluto.local";
            yield break;
        }
        yield return uri.Trim();
    }

    // ---------------------------------------------------------------- discovery

    private void Discover(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new IioException("The Pluto returned a context XML that could not be parsed.", ex);
        }

        var context = doc.Root;
        if (context == null) throw new IioException("Empty IIO context XML.");

        foreach (var attr in context.Elements("context-attribute"))
        {
            string name = (string)attr.Attribute("name");
            string value = (string)attr.Attribute("value");
            switch (name)
            {
                case "hw_model": Model = value; break;
                case "fw_version": FirmwareVersion = value; break;
                case "hw_serial": if (!string.IsNullOrEmpty(value) && value != "0000000000000000") Serial = value; break;
            }
        }

        XElement phy = null, rx = null;
        foreach (var dev in context.Elements("device"))
        {
            string name = (string)dev.Attribute("name");
            if (name == PhyDeviceName) phy = dev;
            if (name == RxDeviceName) rx = dev;
        }

        if (phy == null) throw new IioException($"'{PhyDeviceName}' not found: this does not look like a PlutoSDR context.");
        if (rx == null) throw new IioException($"'{RxDeviceName}' not found: the RX DMA device is missing.");

        _phyId = (string)phy.Attribute("id");
        _rxId = (string)rx.Attribute("id");

        // RX scan channels, in device order: the bit position in the OPEN/READBUF mask.
        var scan = new List<string>();
        int rxChannelCount = 0;
        foreach (var chn in rx.Elements("channel"))
        {
            rxChannelCount++;
            if (chn.Element("scan-element") != null && (string)chn.Attribute("type") == "input")
                scan.Add((string)chn.Attribute("id"));
        }
        if (scan.Count < 2)
            throw new IioException($"Expected 2 RX scan channels (I and Q) but found {scan.Count}.");
        _scanChannels = scan.ToArray();
        _bytesPerSample = _scanChannels.Length * 2;

        int words = (rxChannelCount + 31) / 32;
        _mask = new uint[words];
        for (int i = 0; i < _scanChannels.Length; i++) _mask[i / 32] |= 1u << (i % 32);
        _iio.ChannelMaskWords = words;

        // The RX local oscillator: the output channel carrying a "frequency" attribute.
        foreach (var chn in phy.Elements("channel"))
        {
            if ((string)chn.Attribute("type") != "output") continue;
            bool hasFrequency = chn.Elements("attribute").Any(a => (string)a.Attribute("name") == "frequency");
            string label = (string)chn.Attribute("name");
            bool looksLikeRx = label == "RX_LO" || label == null;
            if (hasFrequency && looksLikeRx)
            {
                _loChannel = (string)chn.Attribute("id");
                break;
            }
        }
        if (_loChannel == null)
        {
            foreach (var chn in phy.Elements("channel"))
            {
                if ((string)chn.Attribute("type") != "output") continue;
                if (chn.Elements("attribute").Any(a => (string)a.Attribute("name") == "frequency"))
                {
                    _loChannel = (string)chn.Attribute("id");
                    break;
                }
            }
        }
        if (_loChannel == null) throw new IioException("RX LO channel not found on " + PhyDeviceName);

        // RX gain lives on the input channel that advertises gain_control_mode.
        foreach (var chn in phy.Elements("channel"))
        {
            if ((string)chn.Attribute("type") != "input") continue;
            if (chn.Elements("attribute").Any(a => (string)a.Attribute("name") == "gain_control_mode"))
            {
                _gainChannel = (string)chn.Attribute("id");
                break;
            }
        }
        if (_gainChannel == null) throw new IioException("RX gain channel not found on " + PhyDeviceName);

        ReadRanges();
    }

    private void ReadRanges()
    {
        long minFrequency = MinFrequency, maxFrequency = MaxFrequency;
        TryRange(ReadChannelAttr(_loChannel, true, "frequency_available"), ref minFrequency, ref maxFrequency);
        MinFrequency = minFrequency;
        MaxFrequency = maxFrequency;

        if (_gainChannel == null) return;

        long minSampleRate = MinSampleRate, maxSampleRate = MaxSampleRate;
        TryRange(ReadChannelAttr(_gainChannel, false, "sampling_frequency_available"), ref minSampleRate, ref maxSampleRate);
        MinSampleRate = minSampleRate;
        MaxSampleRate = maxSampleRate;

        long minBandwidth = MinBandwidth, maxBandwidth = MaxBandwidth;
        TryRange(ReadChannelAttr(_gainChannel, false, "rf_bandwidth_available"), ref minBandwidth, ref maxBandwidth);
        MinBandwidth = minBandwidth;
        MaxBandwidth = maxBandwidth;
    }

    private static void TryRange(string text, ref long min, ref long max)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var numbers = text.Trim('[', ']', ' ')
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (numbers.Length >= 3 &&
            long.TryParse(numbers[0], out long lo) && long.TryParse(numbers[2], out long hi) &&
            lo > 0 && hi > lo)
        {
            min = lo;
            max = hi;
        }
    }

    // ---------------------------------------------------------------- attributes

    private string ReadChannelAttr(string channel, bool output, string attr)
    {
        try { return _iio.ReadChannelAttr(_phyId, output, channel, attr)?.Trim(); }
        catch { return null; }
    }

    private bool WriteChannelAttr(string channel, bool output, string attr, string value)
    {
        int ret = _iio.WriteChannelAttr(_phyId, output, channel, attr, value);
        return ret >= 0;
    }

    private void ApplyAll()
    {
        SetSampleRate(SampleRate, throwOnError: false);
        SetBandwidth(Bandwidth, throwOnError: false);
        SetFrequency(Frequency, throwOnError: false);
        SetGain(GainDb, AgcEnabled, throwOnError: false);
    }

    public bool SetFrequency(long hz, bool throwOnError = true)
    {
        if (hz < MinFrequency) hz = MinFrequency;
        if (hz > MaxFrequency) hz = MaxFrequency;
        Frequency = hz;
        if (_iio == null) return false;
        bool ok = WriteChannelAttr(_loChannel, true, "frequency", hz.ToString(CultureInfo.InvariantCulture));
        if (!ok && throwOnError) throw new IioException($"Failed to set the RX LO to {hz} Hz.");
        return ok;
    }

    public bool SetSampleRate(double hz, bool throwOnError = true)
    {
        if (hz < MinSampleRate) hz = MinSampleRate;
        if (hz > MaxSampleRate) hz = MaxSampleRate;
        SampleRate = hz;
        if (_iio == null) return false;

        string value = ((long)Math.Round(hz)).ToString(CultureInfo.InvariantCulture);
        bool ok = WriteChannelAttr(_gainChannel, false, "sampling_frequency", value);
        if (!ok)
        {
            // Older firmware exposes the rate as a device attribute named sample_rate.
            try { ok = _iio.WriteDeviceAttr(_phyId, "sample_rate", value) >= 0; } catch { ok = false; }
        }
        if (!ok && throwOnError) throw new IioException($"Failed to set the sample rate to {hz} Hz.");
        return ok;
    }

    public bool SetBandwidth(long hz, bool throwOnError = true)
    {
        if (hz < MinBandwidth) hz = MinBandwidth;
        if (hz > MaxBandwidth) hz = MaxBandwidth;
        Bandwidth = hz;
        if (_iio == null) return false;
        bool ok = WriteChannelAttr(_gainChannel, false, "rf_bandwidth", hz.ToString(CultureInfo.InvariantCulture));
        if (!ok && throwOnError) throw new IioException($"Failed to set the RF bandwidth to {hz} Hz.");
        return ok;
    }

    public bool SetGain(int db, bool agc, bool throwOnError = true)
    {
        GainDb = db;
        AgcEnabled = agc;
        if (_iio == null) return false;

        string mode = agc ? "slow_attack" : "manual";
        bool ok = WriteChannelAttr(_gainChannel, false, "gain_control_mode", mode);
        AgcMode = mode;
        if (!agc)
        {
            if (db < -1) db = -1;
            if (db > 73) db = 73;
            GainDb = db;
            ok &= WriteChannelAttr(_gainChannel, false, "hardwaregain", db.ToString(CultureInfo.InvariantCulture));
        }
        if (!ok && throwOnError) throw new IioException("Failed to apply the RX gain settings.");
        return ok;
    }

    public double ReadRssi()
    {
        string text = ReadChannelAttr(_gainChannel, false, "rssi");
        if (text == null) return double.NaN;
        var parts = text.Split(' ');
        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }

    public double ReadHardwareGain()
    {
        string text = ReadChannelAttr(_gainChannel, false, "hardwaregain");
        if (text == null) return double.NaN;
        var parts = text.Split(' ');
        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }

    /// <summary>Reads back what the hardware actually reports, for diagnostics.</summary>
    public string ReadBackSampleRate() => ReadChannelAttr(_gainChannel, false, "sampling_frequency");
    public string ReadBackBandwidth() => ReadChannelAttr(_gainChannel, false, "rf_bandwidth");
    public string ReadBackLoFrequency() => ReadChannelAttr(_loChannel, true, "frequency");
    public string ReadBackPathRates()
    {
        try { return _iio.ReadDeviceAttr(_phyId, "rx_path_rates"); }
        catch { return null; }
    }
    public string ReadBackDeviceAttr(string attr)
    {
        try { return _iio.ReadDeviceAttr(_phyId, attr); }
        catch { return null; }
    }

    // ---------------------------------------------------------------- streaming

    /// <summary>Kernel buffer size handed to the device, in samples.</summary>
    public int BufferSamples { get; set; } = 65536;

    /// <summary>
    /// Size in bytes of one kernel buffer. Each READBUF must drain a whole buffer: the DMA engine
    /// recycles a buffer as soon as it is full, so asking for less silently discards the remainder
    /// (a 1/4-sized read measured exactly 0.5 MSPS out of a 2 MSPS stream).
    /// </summary>
    public int BufferBytes => BufferSamples * _bytesPerSample;

    /// <summary>Chooses a kernel buffer holding roughly <paramref name="seconds"/> of IQ.</summary>
    public void ConfigureBuffering(double seconds = 0.032)
    {
        double rate = SampleRate > 0 ? SampleRate : 2_000_000;
        int samples = (int)(rate * seconds);
        samples &= ~0xFFF;                       // keep it a multiple of 4096 samples
        if (samples < 4096) samples = 4096;
        if (samples > 262144) samples = 262144;
        BufferSamples = samples;
    }

    public void StartStreaming()
    {
        if (_iio == null) throw new IioException("Device is not open.");
        if (_streaming) return;
        int ret = _iio.SetBuffersCount(_rxId, 4);
        _ = ret; // optional on some firmware revisions
        ret = _iio.Open(_rxId, BufferSamples, _mask);
        if (ret < 0) throw new IioException($"IIOD OPEN {_rxId} failed with code {ret}.");
        _streaming = true;
    }

    public void StopStreaming()
    {
        if (_iio == null) return;
        _streaming = false;
        try { _iio.CloseDevice(_rxId); } catch { }
    }

    /// <summary>Bytes per interleaved I/Q sample pair as delivered by the kernel buffer.</summary>
    public int BytesPerSample => _bytesPerSample;

    public long TransportReadCalls => _iio?.TransportReadCalls ?? 0;
    public long TotalChunks => _iio?.TotalChunks ?? 0;
    public int LastChunkCount => _iio?.LastChunkCount ?? 0;
    public int LastChunkMax => _iio?.LastChunkMax ?? 0;

    /// <summary>Pulls one block of raw interleaved samples. Returns the byte count read.</summary>
    public int ReadBlock(byte[] buffer, int length)
    {
        if (_iio == null) throw new IioException("Device is not open.");
        return _iio.ReadBuf(_rxId, buffer, length, null);
    }

    public void Dispose()
    {
        try { StopStreaming(); } catch { }
        _opened = false;
        _iio?.Dispose();
        _iio = null;
    }
}
