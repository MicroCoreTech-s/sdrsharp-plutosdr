using System.Globalization;
using System.Windows.Forms;

namespace SDRSharp.PlutoSDR;

/// <summary>
/// Source configuration panel SDR# shows for the PlutoSDR frontend.
///
/// The panel is laid out as a single full-width column rather than with fixed coordinates: SDR#
/// docks it into a narrow, user-resizable source pane, and a fixed-width layout overflowed that
/// pane and forced a horizontal scrollbar. Every control now follows the available width, and the
/// wrapping labels are measured on resize so nothing can spill sideways.
/// </summary>
internal sealed class PlutoControlPanel : UserControl
{
    private static readonly double[] SampleRates =
    {
        500_000, 1_000_000, 1_536_000, 2_000_000, 2_500_000, 3_072_000, 3_840_000,
        4_000_000, 5_000_000, 6_000_000, 7_680_000, 10_000_000, 15_360_000, 20_000_000, 30_720_000
    };

    private static readonly long[] Bandwidths =
    {
        200_000, 500_000, 1_000_000, 1_500_000, 2_000_000, 2_500_000, 3_000_000, 4_000_000,
        5_000_000, 6_000_000, 8_000_000, 10_000_000, 15_000_000, 20_000_000, 30_000_000, 40_000_000, 56_000_000
    };

    private readonly PlutoSDRIO _owner;
    private readonly TableLayoutPanel _layout = new TableLayoutPanel();

    private readonly Label _statusLabel = new Label();
    private readonly Label _rssiLabel = new Label();
    private readonly Label _uriLabel = new Label();
    private readonly Label _rateLabel = new Label();
    private readonly Label _bandwidthLabel = new Label();
    private readonly Label _gainLabel = new Label();
    private readonly Label _hintLabel = new Label();

    private readonly TextBox _uriBox = new TextBox();
    private readonly Button _reconnectButton = new Button();
    private readonly ComboBox _rateCombo = new ComboBox();
    private readonly ComboBox _bwCombo = new ComboBox();
    private readonly CheckBox _agcCheck = new CheckBox();
    private readonly TrackBar _gainBar = new TrackBar();

    private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
    private bool _updating;

    public PlutoControlPanel(PlutoSDRIO owner)
    {
        _owner = owner;
        BuildLayout();
        LoadValues();

        _timer.Interval = 700;
        _timer.Tick += (s, e) => RefreshTelemetry();
        _timer.Enabled = true;

        OnDeviceChanged();
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        SuspendLayout();

        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
        ForeColor = System.Drawing.Color.Gainsboro;
        Font = new System.Drawing.Font("Segoe UI", 8.25f);
        MinimumSize = new System.Drawing.Size(120, 0);
        Size = new System.Drawing.Size(200, 430);

        _layout.Dock = DockStyle.Top;
        _layout.AutoSize = true;
        _layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _layout.ColumnCount = 1;
        _layout.RowCount = 0;
        _layout.Padding = new Padding(6, 6, 6, 6);
        _layout.Margin = new Padding(0);
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        Controls.Add(_layout);

        // ---- device status -------------------------------------------------
        StyleWrapping(_statusLabel);
        _statusLabel.ForeColor = System.Drawing.Color.FromArgb(150, 200, 255);
        AddRow(_statusLabel);

        // ---- device URI + connect ------------------------------------------
        StyleFieldLabel(_uriLabel, "Device URI");
        AddRow(_uriLabel);

        var uriRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6),
            AutoSize = false
        };
        uriRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        uriRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64f));

        _uriBox.Dock = DockStyle.Fill;
        _uriBox.Margin = new Padding(0, 0, 4, 0);
        uriRow.Controls.Add(_uriBox, 0, 0);

        _reconnectButton.Text = "Connect";
        _reconnectButton.Dock = DockStyle.Fill;
        _reconnectButton.AutoSize = false;
        _reconnectButton.Margin = new Padding(0);
        _reconnectButton.Click += (s, e) => Reconnect();
        uriRow.Controls.Add(_reconnectButton, 1, 0);

        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
        _layout.Controls.Add(uriRow, 0, _layout.RowCount);
        _layout.RowCount++;

        // ---- sample rate ---------------------------------------------------
        StyleFieldLabel(_rateLabel, "Sample rate");
        AddRow(_rateLabel);

        _rateCombo.Dock = DockStyle.Fill;
        _rateCombo.DropDownStyle = ComboBoxStyle.DropDown;
        foreach (double rate in SampleRates) _rateCombo.Items.Add(FormatRate(rate));
        _rateCombo.SelectedIndexChanged += (s, e) => OnRateChanged();
        _rateCombo.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnRateChanged(); }
        };
        AddRow(_rateCombo, 24);

        // ---- RF bandwidth --------------------------------------------------
        StyleFieldLabel(_bandwidthLabel, "RF bandwidth");
        AddRow(_bandwidthLabel);

        _bwCombo.Dock = DockStyle.Fill;
        _bwCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (long bw in Bandwidths) _bwCombo.Items.Add(FormatRate(bw));
        _bwCombo.SelectedIndexChanged += (s, e) => OnBandwidthChanged();
        AddRow(_bwCombo, 24);

        // ---- AGC -----------------------------------------------------------
        _agcCheck.Text = "AGC (slow attack)";
        _agcCheck.Dock = DockStyle.Fill;
        _agcCheck.AutoSize = false;
        _agcCheck.CheckedChanged += (s, e) => OnGainChanged();
        AddRow(_agcCheck, 22);

        // ---- gain ----------------------------------------------------------
        StyleFieldLabel(_gainLabel, "RX gain");
        AddRow(_gainLabel);

        _gainBar.Dock = DockStyle.Fill;
        _gainBar.AutoSize = false;
        _gainBar.Minimum = -1;
        _gainBar.Maximum = 73;
        _gainBar.TickFrequency = 10;
        _gainBar.ValueChanged += (s, e) =>
        {
            _gainLabel.Text = $"RX gain: {_gainBar.Value} dB";
            OnGainChanged();
        };
        AddRow(_gainBar, 32);

        // ---- telemetry -----------------------------------------------------
        StyleWrapping(_rssiLabel);
        AddRow(_rssiLabel);

        // ---- hint ----------------------------------------------------------
        StyleWrapping(_hintLabel);
        _hintLabel.ForeColor = System.Drawing.Color.Gray;
        _hintLabel.Text = "Tip: keep the sample rate at or below ~6 MSPS on USB 2.0.";
        AddRow(_hintLabel);

        ResumeLayout(false);
        PerformLayout();
        UpdateWrapWidths();
    }

    private void AddRow(Control control)
        => AddRow(control, 0f);

    private void AddRow(Control control, float absoluteHeight)
    {
        control.Margin = new Padding(0, 0, 0, 4);
        if (control is Label || control is CheckBox) control.Margin = new Padding(0, 2, 0, 2);

        if (absoluteHeight > 0f)
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, absoluteHeight));
        else
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _layout.Controls.Add(control, 0, _layout.RowCount);
        _layout.RowCount++;
    }

    private static void StyleFieldLabel(Label label, string text)
    {
        label.Text = text;
        label.AutoSize = true;
        label.Dock = DockStyle.Fill;
        label.ForeColor = System.Drawing.Color.Gainsboro;
    }

    private static void StyleWrapping(Label label)
    {
        label.AutoSize = true;
        label.Dock = DockStyle.Fill;
    }

    /// <summary>
    /// Keeps wrapping labels inside the visible width. AutoSize plus a maximum width makes the
    /// label wrap and grow downwards instead of extending sideways, which is what produced the
    /// horizontal scrollbar before.
    /// </summary>
    private void UpdateWrapWidths()
    {
        int width = Math.Max(60, ClientSize.Width - _layout.Padding.Horizontal - 4);
        foreach (Label label in new[] { _statusLabel, _rssiLabel, _hintLabel })
        {
            if (label.MaximumSize.Width == width) continue;
            label.MaximumSize = new System.Drawing.Size(width, 0);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWrapWidths();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        UpdateWrapWidths();
    }

    // ------------------------------------------------------------------ values

    private static string FormatRate(double hz)
    {
        if (hz >= 1_000_000) return (hz / 1_000_000.0).ToString("0.####", CultureInfo.InvariantCulture) + " MSPS";
        return (hz / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " kSPS";
    }

    private static double ParseRate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        string s = text.Trim().ToUpperInvariant();
        double factor = 1;
        if (s.Contains("MSPS") || s.Contains("MHZ")) { factor = 1_000_000; s = s.Replace("MSPS", "").Replace("MHZ", ""); }
        else if (s.Contains("KSPS") || s.Contains("KHZ")) { factor = 1_000; s = s.Replace("KSPS", "").Replace("KHZ", ""); }
        else if (s.Contains("SPS") || s.Contains("HZ")) { s = s.Replace("SPS", "").Replace("HZ", ""); }
        return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v * factor : 0;
    }

    private void LoadValues()
    {
        _updating = true;
        try
        {
            _uriBox.Text = PlutoSettings.GetString("DeviceUri", "auto");
            _rateCombo.Text = FormatRate(_owner.CurrentSampleRate);
            SelectClosest(_bwCombo, Bandwidths, _owner.CurrentBandwidth);
            _agcCheck.Checked = _owner.CurrentAgc;
            int gain = Math.Max(_gainBar.Minimum, Math.Min(_gainBar.Maximum, _owner.CurrentGain));
            _gainBar.Value = gain;
            _gainLabel.Text = $"RX gain: {gain} dB";
            _gainBar.Enabled = !_agcCheck.Checked;
        }
        finally
        {
            _updating = false;
        }
    }

    private static void SelectClosest(ComboBox combo, long[] values, long target)
    {
        int best = 0;
        long bestDelta = long.MaxValue;
        for (int i = 0; i < values.Length; i++)
        {
            long delta = Math.Abs(values[i] - target);
            if (delta < bestDelta) { bestDelta = delta; best = i; }
        }
        combo.SelectedIndex = best;
    }

    // ------------------------------------------------------------------ events

    private void OnRateChanged()
    {
        if (_updating) return;
        double rate = ParseRate(_rateCombo.Text);
        if (rate <= 0) return;
        try { _owner.ApplySampleRate(rate); }
        catch (Exception ex) { ShowError(ex.Message); }
        RefreshTelemetry();
    }

    private void OnBandwidthChanged()
    {
        if (_updating) return;
        if (_bwCombo.SelectedIndex < 0) return;
        try { _owner.ApplyBandwidth(Bandwidths[_bwCombo.SelectedIndex]); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnGainChanged()
    {
        if (_updating) return;
        _gainBar.Enabled = !_agcCheck.Checked;
        try { _owner.ApplyGain(_gainBar.Value, _agcCheck.Checked); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Reconnect()
    {
        try
        {
            _owner.ApplyDeviceUri(_uriBox.Text.Trim());
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        OnDeviceChanged();
    }

    private void ShowError(string message)
    {
        _rssiLabel.ForeColor = System.Drawing.Color.FromArgb(255, 120, 120);
        _rssiLabel.Text = "Error: " + message;
    }

    /// <summary>Refreshes everything after the device was opened or closed.</summary>
    internal void OnDeviceChanged()
    {
        if (InvokeRequired && IsHandleCreated)
        {
            try { BeginInvoke(new Action(OnDeviceChanged)); } catch { }
            return;
        }

        var device = _owner.Device;
        if (device == null || !device.IsOpen)
        {
            _statusLabel.Text = "PlutoSDR: not connected";
            _reconnectButton.Text = "Connect";
            _rssiLabel.ForeColor = System.Drawing.Color.Gainsboro;
            _rssiLabel.Text = _owner.LastError ?? string.Empty;
            _hintLabel.Visible = true;
            UpdateWrapWidths();
            return;
        }

        _statusLabel.Text =
            $"{Abbreviate(device.Model)}\n" +
            $"FW {device.FirmwareVersion}  SN {Shorten(device.Serial)}\n" +
            $"libiio {device.LibiioVersion}\n" +
            $"{Abbreviate(device.TransportDescription)}";
        _reconnectButton.Text = "Reconnect";
        _rssiLabel.ForeColor = System.Drawing.Color.Gainsboro;
        _hintLabel.Visible = true;

        _updating = true;
        try
        {
            _rateCombo.Text = FormatRate(device.SampleRate);
            SelectClosest(_bwCombo, Bandwidths, device.Bandwidth);
            _agcCheck.Checked = device.AgcEnabled;
            int gain = Math.Max(_gainBar.Minimum, Math.Min(_gainBar.Maximum, device.GainDb));
            _gainBar.Value = gain;
            _gainLabel.Text = $"RX gain: {gain} dB";
            _gainBar.Enabled = !device.AgcEnabled;
        }
        finally
        {
            _updating = false;
        }

        UpdateWrapWidths();
    }

    /// <summary>Shortens the verbose hardware strings so they fit the narrow source pane.</summary>
    private static string Abbreviate(string text)
    {
        if (string.IsNullOrEmpty(text)) return "?";

        string result = text
            .Replace("Analog Devices ", string.Empty)
            .Replace(" (Z7010-AD9364)", " (AD9364)")
            .Replace(" via ", "  ")
            ;
        return result;
    }

    private static string Shorten(string value)
        => string.IsNullOrEmpty(value) || value.Length <= 8 ? (value ?? "?") : value.Substring(0, 8);

    private void RefreshTelemetry()
    {
        var device = _owner.Device;
        if (device == null || !device.IsOpen || !Visible) return;

        try
        {
            double rssi = device.ReadRssi();
            double gain = device.ReadHardwareGain();
            _rssiLabel.ForeColor = System.Drawing.Color.Gainsboro;
            _rssiLabel.Text = double.IsNaN(rssi)
                ? (device.IsStreaming ? "streaming" : "idle")
                : $"RSSI {rssi:0.0} dB\ngain {gain:0.0} dB  {(device.IsStreaming ? "streaming" : "idle")}";
        }
        catch
        {
            // A transient IIOD error while streaming is not worth surfacing here.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Enabled = false;
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
