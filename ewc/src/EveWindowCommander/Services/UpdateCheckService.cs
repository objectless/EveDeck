using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;

namespace EveWindowCommander.Services;

public record VersionInfo(string Version, string DownloadUrl, string[]? WhatsNew);

public static class UpdateCheckService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "EveDeck-UpdateCheck/1.0" } }
    };

    private const string VersionUrl = "https://evedeck.space/api/version";

    public static async Task<VersionInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<VersionInfo>(VersionUrl, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNewer(string remoteVersion)
    {
        var local = Assembly.GetExecutingAssembly().GetName().Version;
        return local is not null
            && System.Version.TryParse(remoteVersion, out var remote)
            && remote > local;
    }
}
