using System.Net.Http;
using System.Text.Json;

namespace EveWindowCommander.Services;

public sealed class UpdateCheckService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string ManifestUrl = "https://evedeck.space/api/version";

    private readonly LogService? _log;

    public UpdateCheckService(LogService? log = null)
    {
        _log = log;
    }

    public record UpdateInfo(string Version, string DownloadUrl);

    public async Task<UpdateInfo?> CheckAsync(string currentVersion)
    {
        try
        {
            var json = await Http.GetStringAsync(ManifestUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var remote = root.GetProperty("version").GetString() ?? "";
            var url = root.GetProperty("downloadUrl").GetString() ?? "";
            if (IsNewer(remote, currentVersion))
                return new UpdateInfo(remote, url);
        }
        catch (Exception ex)
        {
            _log?.Warn($"Update check failed: {ex.Message}");
        }
        return null;
    }

    private static bool IsNewer(string remote, string current)
    {
        return Version.TryParse(remote, out var r) &&
               Version.TryParse(current, out var c) &&
               r > c;
    }
}
