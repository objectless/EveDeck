using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EveDeck.Services;

// On-disk cache of ship-type icons (ESI's public images.evetech.net CDN, no auth) -- one small PNG
// per type id, cached forever (a ship's icon never changes). Mirrors PortraitCacheService's
// decode-and-Freeze() convention but is simpler: no name resolution, no staleness/TTL, and the
// bulk-fetch step (EnsureIconsCachedAsync) is meant to be run once after the ship dictionary itself
// is available (crawled or loaded from a bundled seed -- see MainWindowViewModel.IntelJumpAlert.cs)
// rather than lazily per-toast, so a resolved ship's icon is normally already on disk by the time
// any real intel alert fires.
public sealed class ShipIconCacheService
{
    private const int IconSize = 64;
    private const int MaxConcurrency = 16;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _dir;
    private readonly ConcurrentDictionary<int, ImageSource> _memoryCache = new();

    public ShipIconCacheService(string appDataFolder)
    {
        _dir = Path.Combine(appDataFolder, "cache", "ship-icons");
        try { Directory.CreateDirectory(_dir); } catch { /* first read/write will surface the error */ }
    }

    private string PathFor(int typeId) => Path.Combine(_dir, $"{typeId}.png");
    private static string UrlFor(int typeId) => $"https://images.evetech.net/types/{typeId}/icon?size={IconSize}";

    // Synchronous, cheap: returns the icon if it's already cached (memory or disk), else null --
    // never blocks on a network fetch. Callers show a fallback (e.g. a plain accent dot) for null.
    public ImageSource? TryGetCachedIcon(int typeId)
    {
        if (_memoryCache.TryGetValue(typeId, out var cached)) return cached;

        var path = PathFor(typeId);
        if (!File.Exists(path)) return null;

        try
        {
            var image = LoadFrozen(path);
            _memoryCache[typeId] = image;
            return image;
        }
        catch
        {
            return null; // corrupt cache file -- a later EnsureIconsCachedAsync pass overwrites it
        }
    }

    // Fetches whatever icons aren't already cached on disk, bounded parallelism. Meant to run once
    // right after the ship dictionary becomes available; idempotent and safe to call again (e.g.
    // after a manual "Build system map" refresh picks up newly-released ships). A single icon
    // failing never aborts the batch.
    public async Task EnsureIconsCachedAsync(IEnumerable<int> typeIds, CancellationToken ct)
    {
        var missing = typeIds.Distinct().Where(id => !File.Exists(PathFor(id))).ToList();
        if (missing.Count == 0) return;

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct };
        await Parallel.ForEachAsync(missing, parallelOptions, async (typeId, token) =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(UrlFor(typeId), token).ConfigureAwait(false);
                if (bytes.Length == 0) return;

                var path = PathFor(typeId);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, token).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // Best-effort -- a ship this fails for just shows the fallback dot until a later pass.
            }
        });
    }

    private static BitmapImage LoadFrozen(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.DecodePixelWidth = IconSize;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
