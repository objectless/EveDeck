using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using EveDeck.Models;

namespace EveDeck.Services;

// On-disk portrait cache. WPF's default BitmapImage-from-URL path caches portraits in WinINET
// indefinitely, so a character who changes their in-game portrait (or a freshly created character)
// keeps showing the stale image forever. This service owns freshness instead: it downloads each
// portrait once to %LOCALAPPDATA%\EveDeck\cache\portraits\{id}.png with HttpClient (never WinINET),
// re-downloads files older than the TTL, and hands every UI surface a single shared observable
// CharacterPortrait per character id so a refreshed image appears everywhere at once.
//
// It also resolves a running character NAME -> id via the public ESI /universe/ids endpoint (no
// auth), so the corner-overlay labels can show whoever is actually logged into a seat -- including
// unlinked character-set alts -- rather than the seat's configured main character.
public sealed class PortraitCacheService
{
    public static PortraitCacheService Instance { get; } = new();

    private const int PortraitSize = 128;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _dir;
    private readonly ConcurrentDictionary<long, CharacterPortrait> _byId = new();
    private readonly ConcurrentDictionary<long, byte> _inflight = new();
    // Resolved name -> id (id 0 = confirmed "no such character", cached so we don't retry forever).
    private readonly ConcurrentDictionary<string, long> _nameToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _nameInflight = new(StringComparer.OrdinalIgnoreCase);

    // Raised (on the UI thread) whenever a portrait image lands or a running-name resolves, so
    // non-data-bound surfaces (the corner-overlay label window) can re-read their portraits.
    public event Action? Changed;

    private PortraitCacheService()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EveDeck", "cache", "portraits");
        try { Directory.CreateDirectory(_dir); } catch { /* first read/write will surface the error */ }
    }

    private string PathFor(long id) => Path.Combine(_dir, $"{id}.png");

    private static string UrlFor(long id) =>
        $"https://images.evetech.net/characters/{id}/portrait?size={PortraitSize}";

    // The shared observable portrait for a character id. Loads any on-disk copy immediately (so a
    // returning user sees faces without a flash) and schedules a background download when the file
    // is missing or older than the TTL.
    public CharacterPortrait ForId(long id)
    {
        if (id <= 0) return new CharacterPortrait(id);

        if (_byId.TryGetValue(id, out var existing)) return existing;

        var portrait = _byId.GetOrAdd(id, cid => new CharacterPortrait(cid));

        // Only do real work inside a running WPF app (keeps unit tests / headless paths inert).
        if (Application.Current is null) return portrait;

        var path = PathFor(id);
        var exists = File.Exists(path);
        if (exists && portrait.Image is null)
        {
            try { portrait.Image = LoadFrozen(path); } catch { /* corrupt cache file -> re-download below */ }
        }

        if (!exists || IsStale(path)) _ = DownloadAsync(id, force: false);
        return portrait;
    }

    // The shared observable portrait for a character NAME. Returns null until the name resolves to an
    // id (kicking off a cached ESI lookup); callers fall back to another portrait meanwhile.
    public CharacterPortrait? ForName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_nameToId.TryGetValue(name, out var id)) return id > 0 ? ForId(id) : null;

        if (Application.Current is not null) _ = ResolveNameAsync(name);
        return null;
    }

    // Manual "Refresh portraits": force a re-download of every portrait we currently know about and
    // clear negative name lookups so newly created characters can resolve.
    public void RefreshAll()
    {
        if (Application.Current is null) return;
        foreach (var name in _nameToId.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList())
            _nameToId.TryRemove(name, out _);
        foreach (var id in _byId.Keys.ToList())
            _ = DownloadAsync(id, force: true);
    }

    // Ensure portraits for a set of ids are present and fresh (used on startup / after edits and by
    // the hourly staleness sweep). Cheap and idempotent for ids already loaded.
    public void Warm(IEnumerable<long> ids)
    {
        foreach (var id in ids) ForId(id);
    }

    private bool IsStale(string path)
    {
        try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > Ttl; }
        catch { return true; }
    }

    private async Task DownloadAsync(long id, bool force)
    {
        if (!_inflight.TryAdd(id, 0)) return; // already downloading
        try
        {
            var path = PathFor(id);
            if (!force && File.Exists(path) && !IsStale(path)) return;

            var bytes = await Http.GetByteArrayAsync(UrlFor(id)).ConfigureAwait(false);
            if (bytes.Length == 0) return;

            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);

            var frozen = LoadFrozen(path);
            AssignOnUi(id, frozen);
        }
        catch { /* transient network / IO error: keep whatever image is already showing */ }
        finally { _inflight.TryRemove(id, out _); }
    }

    private async Task ResolveNameAsync(string name)
    {
        if (!_nameInflight.TryAdd(name, 0)) return;
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(new[] { name }), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(
                "https://esi.evetech.net/latest/universe/ids/?datasource=tranquility", body).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            long resolved = 0;
            if (doc.RootElement.TryGetProperty("characters", out var chars) && chars.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in chars.EnumerateArray())
                {
                    if (c.TryGetProperty("name", out var n)
                        && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase)
                        && c.TryGetProperty("id", out var idEl))
                    {
                        resolved = idEl.GetInt64();
                        break;
                    }
                }
            }

            _nameToId[name] = resolved;
            if (resolved > 0)
            {
                ForId(resolved);          // begins loading/downloading the portrait
                RaiseChangedOnUi();       // let RunningPortrait recompute to the resolved id
            }
        }
        catch { /* leave the name unresolved; a later refresh can retry */ }
        finally { _nameInflight.TryRemove(name, out _); }
    }

    private static BitmapImage LoadFrozen(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;              // read the file now, don't lock it
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // pick up a re-downloaded file
        bmp.DecodePixelWidth = PortraitSize;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();                                            // shareable across UI + label surface
        return bmp;
    }

    private void AssignOnUi(long id, ImageSource image)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null) return;
        disp.Invoke(() =>
        {
            if (_byId.TryGetValue(id, out var portrait)) portrait.Image = image;
            Changed?.Invoke();
        });
    }

    private void RaiseChangedOnUi()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null) return;
        disp.Invoke(() => Changed?.Invoke());
    }
}
