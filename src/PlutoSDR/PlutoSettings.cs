using System.Globalization;
using System.Text;

namespace SDRSharp.PlutoSDR;

/// <summary>Small key=value store kept beside the plugin assembly.</summary>
internal static class PlutoSettings
{
    private static readonly string FilePath = Path.Combine(
        Path.GetDirectoryName(typeof(PlutoSettings).Assembly.Location) ?? ".",
        "PlutoSDR.config");

    private static readonly Dictionary<string, string> Values = Load();

    private static Dictionary<string, string> Load()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return dict;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                int eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                dict[trimmed.Substring(0, eq).Trim()] = trimmed.Substring(eq + 1).Trim();
            }
        }
        catch { }
        return dict;
    }

    public static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# SDR# PlutoSDR plugin settings");
            foreach (var kv in Values) sb.AppendLine($"{kv.Key}={kv.Value}");
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch { }
    }

    public static string GetString(string key, string fallback)
        => Values.TryGetValue(key, out string v) && !string.IsNullOrEmpty(v) ? v : fallback;

    public static void SetString(string key, string value) => Values[key] = value;

    public static bool GetBool(string key, bool fallback)
        => Values.TryGetValue(key, out string v) && bool.TryParse(v, out bool b) ? b : fallback;

    public static void SetBool(string key, bool value)
        => Values[key] = value ? "true" : "false";

    public static int GetInt(string key, int fallback)
        => Values.TryGetValue(key, out string v) &&
           int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : fallback;

    public static void SetInt(string key, int value)
        => Values[key] = value.ToString(CultureInfo.InvariantCulture);

    public static double GetDouble(string key, double fallback)
        => Values.TryGetValue(key, out string v) &&
           double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : fallback;

    public static void SetDouble(string key, double value)
        => Values[key] = value.ToString("R", CultureInfo.InvariantCulture);
}
