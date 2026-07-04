using System.Text.RegularExpressions;

namespace EveDeck.Models;

public record SettingsBackup(DateTime Timestamp, string Path, int Slots, int Chars)
{
    // Filename format: settings_backup_yyyy-MM-ddTHH-mm-ss_sXcY.json
    private static readonly Regex _nameRe = new(
        @"settings_backup_(\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2})(?:_s(\d+)c(\d+))?\.json$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string DisplayName
    {
        get
        {
            var ts = $"{Timestamp:yyyy-MM-dd  HH:mm:ss}";
            if (Slots > 0 || Chars > 0)
                ts += $"  [{Slots} slots, {Chars} chars]";
            return ts;
        }
    }

    public static string BuildFileName(DateTime ts, int slots, int chars)
        => $"settings_backup_{ts:yyyy-MM-ddTHH-mm-ss}_s{slots}c{chars}.json";

    public static SettingsBackup? FromPath(string path)
    {
        var m = _nameRe.Match(System.IO.Path.GetFileName(path));
        if (!m.Success) return null;
        if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-ddTHH-mm-ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var ts))
            ts = System.IO.File.GetLastWriteTime(path);
        int.TryParse(m.Groups[2].Value, out var slots);
        int.TryParse(m.Groups[3].Value, out var chars);
        return new SettingsBackup(ts, path, slots, chars);
    }
}
