using System.Text;

namespace SDRSharp.PlutoSDR;

/// <summary>
/// Small rolling log written next to the plugin. SDR# gives plugins no console, and the host
/// swallows exceptions raised while it opens a source, so this file is the only way to see what
/// the frontend actually did inside the running application.
/// </summary>
internal static class PlutoLog
{
    private static readonly object Sync = new object();
    private static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(typeof(PlutoLog).Assembly.Location) ?? ".",
        "PlutoSDR.log");

    private const long MaxBytes = 512 * 1024;

    public static string FilePath => Path;

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                    File.Delete(Path);

                File.AppendAllText(Path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    public static void Write(string message, Exception ex)
        => Write($"{message}: {ex.GetType().Name}: {ex.Message}");
}
