using System.Globalization;
using SDRSharp.Radio;

namespace SDRSharp.PlutoSDR;

/// <summary>
/// SDR# frontend for the ADALM-PLUTO. Implements the radio interfaces SDR# probes for when it
/// instantiates a source: streaming, tuning, sample-rate change notification, spectrum ratio,
/// host control access and a configuration panel.
/// </summary>
public unsafe class PlutoSDRIO : IFrontendController, IIQStreamController, ITunableSource,
    ISampleRateChangeSource, ISpectrumProvider, IControlAwareObject, IConfigurationPanelProvider
{
    private const int MinBlockBytes = 8 * 1024;
    private const int MaxBlockBytes = 512 * 1024;

    private readonly object _sync = new object();
    private PlutoDevice _device;
    private PlutoControlPanel _panel;
    private object _control;

    private Thread _thread;
    private volatile bool _running;
    private SamplesAvailableDelegate _callback;

    private long _frequency;
    private double _sampleRate = 2_000_000;
    private float _usableSpectrumRatio = 1.0f;
    private string _lastError;
    private long _blocksDelivered;

    public event EventHandler SampleRateChanged;

    public PlutoSDRIO()
    {
        _frequency = (long)PlutoSettings.GetDouble("Frequency", 100_000_000);
        _sampleRate = PlutoSettings.GetDouble("SampleRate", 2_000_000);
    }

    public string LastError => _lastError;
    internal PlutoDevice Device => _device;

    // ------------------------------------------------------------------ IFrontendController

    public void Open()
    {
        lock (_sync)
        {
            if (_device != null) return;

            string uri = PlutoSettings.GetString("DeviceUri", "auto");
            var device = new PlutoDevice();
            device.SampleRate = _sampleRate;
            device.Frequency = _frequency;
            device.GainDb = PlutoSettings.GetInt("GainDb", 40);
            device.AgcEnabled = PlutoSettings.GetBool("Agc", false);
            device.Bandwidth = (long)PlutoSettings.GetDouble("Bandwidth", _sampleRate);

            try
            {
                device.Open(uri);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                PlutoLog.Write($"Open('{uri}') failed", ex);
                throw;
            }

            _device = device;
            _frequency = device.Frequency;
            _sampleRate = device.SampleRate;
            _usableSpectrumRatio = 1.0f;
            _lastError = null;
            PlutoLog.Write($"opened {device.Model} fw {device.FirmwareVersion} via {device.TransportDescription}; " +
                           $"rate={device.SampleRate} LO={device.Frequency} gain={device.GainDb}dB");
        }

        _panel?.OnDeviceChanged();
        SampleRateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        Stop();
        lock (_sync)
        {
            if (_device != null) PlutoLog.Write("closing device");
            _device?.Dispose();
            _device = null;
        }
        _panel?.OnDeviceChanged();
    }

    // ------------------------------------------------------------------ IIQStreamController

    public double Samplerate => _sampleRate;

    public void Start(SamplesAvailableDelegate del)
    {
        lock (_sync)
        {
            if (_device == null) Open();
            if (_device == null) throw new InvalidOperationException("PlutoSDR is not connected.");

            StopThread();
            _callback = del;
            _device.ConfigureBuffering();
            _device.StartStreaming();
            _running = true;
            PlutoLog.Write($"streaming started: buffer {_device.BufferSamples} samples " +
                           $"({_device.BufferBytes} bytes per block), rate {_device.SampleRate}");

            _thread = new Thread(StreamLoop)
            {
                IsBackground = true,
                Name = "PlutoSDR IQ stream",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopThread();
        }
    }

    private void StopThread()
    {
        _running = false;
        Thread thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive)
        {
            try { thread.Join(1500); } catch { }
        }
        try { _device?.StopStreaming(); } catch { }
    }

    private void StreamLoop()
    {
        PlutoDevice device = _device;
        SamplesAvailableDelegate callback = _callback;
        if (device == null || callback == null) return;

        int blockBytes = device.BufferBytes;
        var raw = new byte[blockBytes];
        int samplesPerBlock = blockBytes / 4;

        UnsafeBuffer output = null;
        try
        {
            output = UnsafeBuffer.Create(samplesPerBlock, sizeof(Complex), false);
            var destination = (Complex*)output.Address;

            while (_running)
            {
                int got;
                try
                {
                    got = device.ReadBlock(raw, blockBytes);
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    PlutoLog.Write("stream read failed", ex);
                    break;
                }

                if (got < 4)
                {
                    if (got <= 0 && !_running) break;
                    continue;
                }

                int count = got / 4;
                const float scale = 1.0f / 2048.0f; // le:S12/16>>0 -> 12-bit signed samples
                for (int i = 0, o = 0; i < count; i++, o += 4)
                {
                    short iSample = (short)(raw[o] | (raw[o + 1] << 8));
                    short qSample = (short)(raw[o + 2] | (raw[o + 3] << 8));
                    destination[i].Real = iSample * scale;
                    destination[i].Imag = qSample * scale;
                }

                try
                {
                    callback(this, destination, count);
                    _blocksDelivered++;
                    if (_blocksDelivered == 1)
                        PlutoLog.Write($"first block delivered to SDR#: {count} IQ samples at {device.SampleRate} SPS");
                    else if (_blocksDelivered % 400 == 0)
                        PlutoLog.Write($"streaming: {_blocksDelivered} blocks delivered ({count} samples each)");
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    PlutoLog.Write("SDR# sample callback threw", ex);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
        finally
        {
            output?.Dispose();
            _running = false;
        }
    }

    private static int ComputeBlockBytes(double sampleRate)
    {
        // Kept for reference: the block size must match the kernel buffer exactly, so the actual
        // value comes from PlutoDevice.BufferBytes instead of a duration estimate.
        double bytes = sampleRate * 4.0 * 0.04;
        int value = (int)bytes;
        value = value & ~3;
        if (value < MinBlockBytes) value = MinBlockBytes;
        if (value > MaxBlockBytes) value = MaxBlockBytes;
        return value;
    }

    // ------------------------------------------------------------------ ITunableSource

    public bool CanTune => true;

    public long Frequency
    {
        get => _frequency;
        set
        {
            _frequency = value;
            ApplyFrequency(value);
            var device = _device;
            if (device == null) return;
            try { device.SetFrequency(value); }
            catch (Exception ex) { _lastError = ex.Message; }
        }
    }

    public long MinimumTunableFrequency => _device?.MinFrequency ?? 70_000_000;
    public long MaximumTunableFrequency => _device?.MaxFrequency ?? 6_000_000_000;

    // ------------------------------------------------------------------ ISpectrumProvider / IControlAwareObject

    public float UsableSpectrumRatio => _usableSpectrumRatio;

    public void SetControl(object control) => _control = control;

    public UserControl Gui => _panel ??= new PlutoControlPanel(this);

    // ------------------------------------------------------------------ settings applied from the panel

    internal void ApplySampleRate(double rate)
    {
        var device = _device;
        if (device == null) return;
        device.SetSampleRate(rate);
        _sampleRate = device.SampleRate;
        PlutoSettings.SetDouble("SampleRate", _sampleRate);
        PlutoSettings.Save();
        SampleRateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ApplyBandwidth(long bandwidth)
    {
        var device = _device;
        if (device == null) return;
        device.SetBandwidth(bandwidth);
        PlutoSettings.SetDouble("Bandwidth", device.Bandwidth);
        PlutoSettings.Save();
    }

    internal void ApplyGain(int db, bool agc)
    {
        PlutoSettings.SetInt("GainDb", db);
        PlutoSettings.SetBool("Agc", agc);
        PlutoSettings.Save();
        var device = _device;
        if (device == null) return;
        device.SetGain(db, agc);
    }

    internal void ApplyDeviceUri(string uri)
    {
        PlutoSettings.SetString("DeviceUri", uri);
        PlutoSettings.Save();
        Close();
        Open();
    }

    internal void ApplyFrequency(long hz)
    {
        PlutoSettings.SetDouble("Frequency", hz);
        PlutoSettings.Save();
    }

    internal double CurrentSampleRate => _sampleRate;
    internal long CurrentBandwidth => _device?.Bandwidth ?? (long)_sampleRate;
    internal int CurrentGain => _device?.GainDb ?? PlutoSettings.GetInt("GainDb", 40);
    internal bool CurrentAgc => _device?.AgcEnabled ?? PlutoSettings.GetBool("Agc", false);
}
