using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EveWindowCommander.Services;

public sealed class EveSettingsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Regex CharFileRegex = new(@"^core_char_(\d+)\.dat$", RegexOptions.IgnoreCase);

    public IReadOnlyList<string> FindSettingsFolders()
    {
        var result = new List<string>();
        var ccp = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCP", "EVE");

        if (!Directory.Exists(ccp)) return result;

        foreach (var serverDir in Directory.GetDirectories(ccp))
        {
            foreach (var settingsDir in Directory.GetDirectories(serverDir, "settings_*"))
                result.Add(settingsDir);
        }

        return result;
    }

    public static string GetFolderDisplayName(string folderPath)
    {
        var parts = folderPath.Split(Path.DirectorySeparatorChar);
        var n = parts.Length;
        var serverPart = n >= 2 ? parts[n - 2] : "";
        var settingsPart = n >= 1 ? parts[n - 1] : folderPath;

        var server = serverPart.Contains("tranquility", StringComparison.OrdinalIgnoreCase) ? "Tranquility"
                   : serverPart.Contains("singularity", StringComparison.OrdinalIgnoreCase) ? "Singularity"
                   : serverPart;

        var resolution = settingsPart.StartsWith("settings_", StringComparison.OrdinalIgnoreCase)
            ? settingsPart[9..].Replace("x", "×")
            : settingsPart;

        return $"{server} · {resolution}";
    }

    public IReadOnlyList<string> GetCharacterFiles(string settingsFolder)
    {
        if (!Directory.Exists(settingsFolder)) return [];
        return [.. Directory.GetFiles(settingsFolder, "core_char_*.dat")
            .Where(f => CharFileRegex.IsMatch(Path.GetFileName(f)))
            .OrderBy(f => f)];
    }

    public static string? GetCharacterId(string filePath)
    {
        var match = CharFileRegex.Match(Path.GetFileName(filePath));
        return match.Success ? match.Groups[1].Value : null;
    }

    public async Task<string?> ResolveCharacterNameAsync(string characterId, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://esi.evetech.net/latest/characters/{characterId}/?datasource=tranquility";
            var json = await Http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("name").GetString();
        }
        catch
        {
            return null;
        }
    }

    // Returns null on success, or an error message on failure.
    public string? CopyProfile(string sourcePath, string targetPath)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                var backupPath = Path.Combine(
                    Path.GetDirectoryName(targetPath)!,
                    Path.GetFileNameWithoutExtension(targetPath)
                        + $"_evedeck_backup_{DateTime.Now:yyyyMMdd_HHmmss}.dat");
                File.Copy(targetPath, backupPath, overwrite: false);
            }
            File.Copy(sourcePath, targetPath, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
