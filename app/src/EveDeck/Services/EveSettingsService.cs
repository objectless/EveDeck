using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EveDeck.Services;

public sealed class EveSettingsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Regex CharFileRegex = new(@"^core_char_(\d+)\.dat$", RegexOptions.IgnoreCase);
    private static readonly Regex UserFileRegex = new(@"^core_user_(\d+)\.dat$", RegexOptions.IgnoreCase);

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

    // Per-account files. Window positions and other account-scoped UI state live here, NOT in
    // core_char -- copying only the char files leaves window layouts diverging across clients.
    public IReadOnlyList<string> GetUserFiles(string settingsFolder)
    {
        if (!Directory.Exists(settingsFolder)) return [];
        return [.. Directory.GetFiles(settingsFolder, "core_user_*.dat")
            .Where(f => UserFileRegex.IsMatch(Path.GetFileName(f)))
            .OrderBy(f => f)];
    }

    public static string? GetUserId(string filePath)
    {
        var match = UserFileRegex.Match(Path.GetFileName(filePath));
        return match.Success ? match.Groups[1].Value : null;
    }

    // Pairs each core_char file with the core_user (account) file whose last-write time is
    // closest. EVE writes both files together when a client logs out, so the mtimes of a
    // char/user pair land within seconds of each other. ESI cannot provide this mapping --
    // accounts are deliberately not exposed by any public API -- so mtime correlation is the
    // only automatic signal available. Pairs outside the tolerance are left unmapped.
    public static IReadOnlyDictionary<string, string> PairCharactersToAccounts(
        IEnumerable<string> charFiles,
        IEnumerable<string> userFiles,
        TimeSpan? tolerance = null)
    {
        var tol = tolerance ?? TimeSpan.FromSeconds(60);
        var users = userFiles
            .Select(f => (Id: GetUserId(f), Time: File.GetLastWriteTimeUtc(f)))
            .Where(u => u.Id is not null)
            .ToList();

        var result = new Dictionary<string, string>();
        if (users.Count == 0) return result;

        foreach (var charFile in charFiles)
        {
            var charId = GetCharacterId(charFile);
            if (charId is null) continue;

            var charTime = File.GetLastWriteTimeUtc(charFile);
            var best = users.MinBy(u => (u.Time - charTime).Duration());
            if ((best.Time - charTime).Duration() <= tol)
                result[charId] = best.Id!;
        }

        return result;
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
