using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SDRSharp.PlutoSDR;

// Renders the real PlutoControlPanel at several widths so its layout can be inspected
// deterministically, without SDR# and without a human clicking anything.
//
// Usage: PanelShot [outputDir] [widths...]      default widths: 200 240 300

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "shots");
        var widths = new List<int>();
        for (int i = 1; i < args.Length; i++)
            if (int.TryParse(args[i], out int w)) widths.Add(w);
        if (widths.Count == 0) widths.AddRange(new[] { 200, 240, 300 });

        Directory.CreateDirectory(outDir);

        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, asm, path) =>
        {
            if (name.StartsWith("libusb", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(AppContext.BaseDirectory, name);
                if (File.Exists(candidate)) return NativeLibrary.Load(candidate);
            }
            return IntPtr.Zero;
        });

        Application.EnableVisualStyles();

        // Connect to the real device so the panel renders its "connected" state.
        var radio = new PlutoSDRIO();
        bool connected = false;
        try
        {
            radio.Open();
            connected = radio.Device is { IsOpen: true };
            Console.WriteLine(connected
                ? $"[+] connected: {radio.Device.Model} via {radio.Device.TransportDescription}"
                : "[!] not connected; rendering the disconnected state");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] open failed: " + ex.Message);
        }

        int exitCode = 0;
        foreach (int width in widths)
        {
            try
            {
                RenderOne(radio, width, Path.Combine(outDir, $"panel-{width}.png"));
                Console.WriteLine($"[ok] {width}px -> panel-{width}.png");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[X] {width}px failed: {ex.Message}");
                exitCode = 1;
            }
        }

        try { radio.Close(); } catch { }
        return connected ? exitCode : 2;
    }

    static void RenderOne(PlutoSDRIO radio, int width, string path)
    {
        // Reach the panel the plug-in hands to SDR#.
        PropertyInfo guiProperty = typeof(PlutoSDRIO).GetProperty("Gui");
        var panel = (UserControl)guiProperty.GetValue(radio);

        var host = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),   // off-screen
            ClientSize = new Size(width, 560),
            BackColor = Color.FromArgb(45, 45, 48)
        };

        panel.Parent = null;          // detach from any previous host
        panel.Dock = DockStyle.Fill;
        host.Controls.Add(panel);
        host.Show();

        // Let layout settle and give the telemetry timer a chance to tick.
        for (int i = 0; i < 45; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }

        ReportScrollState(panel, width);

        using var bitmap = new Bitmap(host.ClientSize.Width, host.ClientSize.Height);
        host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, host.ClientSize));
        bitmap.Save(path, ImageFormat.Png);

        host.Controls.Remove(panel);
        host.Close();
        host.Dispose();
    }

    static void ReportScrollState(Control panel, int requestedWidth)
    {
        // A horizontal scrollbar is exactly the symptom being fixed; report it explicitly.
        int contentWidth = 0;
        foreach (Control child in panel.Controls)
            contentWidth = Math.Max(contentWidth, child.Right + child.Margin.Right);

        bool horizontal = false, vertical = false;
        if (panel is ScrollableControl scrollable)
        {
            horizontal = scrollable.HorizontalScroll.Visible;
            vertical = scrollable.VerticalScroll.Visible;
        }
        Console.WriteLine($"      panel {panel.Width}px  content {contentWidth}px  " +
                          $"hscroll={horizontal}  vscroll={vertical}");
        if (horizontal)
            Console.WriteLine("      !! horizontal scrollbar would appear here");
    }
}
