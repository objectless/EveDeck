using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using EveDeck.Models;

namespace EveDeck.Services;

// Crawls ESI's PUBLIC universe endpoints (no auth) to build the set of every real, currently-flyable
// ship type (id + name), then caches it to disk forever -- the ship roster changes maybe a few times
// a year (new hull releases), nowhere near often enough to justify re-crawling on every launch.
// Mirrors SystemJumpGraphService's exact shape/conventions (same cache-folder, same "never abort the
// whole crawl over one bad fetch" posture) for the sibling "what's a ship" lookup used to tell a ship
// apart from a pilot name in intel trailing text -- see IntelSystemTokenizer.ResolvePilotAndShip. The
// id is kept (not just the name) so a resolved ship's icon can be fetched -- see ShipIconCacheService.
public sealed class ShipTypeDictionaryService
{
    private const string BaseUrl = "https://esi.evetech.net/latest";
    private const int ShipCategoryId = 6;
    private const int MaxConcurrency = 24;
    private const int NamesBatchSize = 1000; // ESI's documented max ids per /universe/names/ call

    private static readonly HttpClient _http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _cachePath;

    public ShipTypeDictionaryService(string appDataFolder)
    {
        var dir = Path.Combine(appDataFolder, "cache");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "ship-type-names.json");
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EveDeck/Intel (github.com/objectless/EveDeck)");
        return http;
    }

    // Loads the on-disk cache without touching the network. Null if missing/corrupt/empty -- caller
    // should fall back to BuildAsync (or a bundled seed file -- see TryLoadFrom).
    public ShipTypeDictionary? TryLoadCached() => TryLoadFrom(_cachePath);

    // Loads a ship-type list from an arbitrary path, same JSON shape as the cache file -- used to
    // load the seed file bundled with the app (see MainWindowViewModel.IntelJumpAlert.cs) without
    // duplicating the parsing logic. Null if missing/corrupt/empty.
    public static ShipTypeDictionary? TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var entries = JsonSerializer.Deserialize<List<ShipTypeEntry>>(File.ReadAllText(path), JsonOptions);
            if (entries is null || entries.Count == 0) return null;
            return new ShipTypeDictionary(entries);
        }
        catch
        {
            return null;
        }
    }

    // Ship category (id 6) -> its groups (Frigate, Cruiser, ...) -> each group's published type ids
    // -> batch-resolved to (id, name) pairs. A single group/batch failing never aborts the whole crawl.
    public async Task<ShipTypeDictionary> BuildAsync(CancellationToken ct)
    {
        var groupIds = await FetchShipGroupIdsAsync(ct);

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct };
        var typeIdsByGroup = new System.Collections.Concurrent.ConcurrentBag<int>();

        await Parallel.ForEachAsync(groupIds, parallelOptions, async (groupId, token) =>
        {
            foreach (var typeId in await FetchPublishedTypeIdsAsync(groupId, token))
                typeIdsByGroup.Add(typeId);
        });

        var typeIds = typeIdsByGroup.Distinct().ToList();
        var entries = new System.Collections.Concurrent.ConcurrentBag<ShipTypeEntry>();
        var batches = typeIds
            .Select((id, i) => (id, batch: i / NamesBatchSize))
            .GroupBy(x => x.batch, x => x.id)
            .Select(g => g.ToList())
            .ToList();

        await Parallel.ForEachAsync(batches, parallelOptions, async (batch, token) =>
        {
            foreach (var entry in await FetchEntriesAsync(batch, token))
                entries.Add(entry);
        });

        var entryList = entries
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .ToList();
        SaveCache(entryList);
        return new ShipTypeDictionary(entryList);
    }

    private async Task<List<int>> FetchShipGroupIdsAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/universe/categories/{ShipCategoryId}/?datasource=tranquility", ct);
            var root = JsonDocument.Parse(json).RootElement;
            var ids = new List<int>();
            if (root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                foreach (var g in groups.EnumerateArray())
                    ids.Add(g.GetInt32());
            return ids;
        }
        catch
        {
            return new List<int>();
        }
    }

    // Only published groups contribute -- an unpublished group is a removed/legacy ship class, not
    // something a real pilot could actually be flying.
    private async Task<List<int>> FetchPublishedTypeIdsAsync(int groupId, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/universe/groups/{groupId}/?datasource=tranquility", ct);
            var root = JsonDocument.Parse(json).RootElement;
            var published = !root.TryGetProperty("published", out var p) || p.GetBoolean();
            if (!published) return new List<int>();

            var ids = new List<int>();
            if (root.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
                foreach (var t in types.EnumerateArray())
                    ids.Add(t.GetInt32());
            return ids;
        }
        catch
        {
            return new List<int>();
        }
    }

    private async Task<List<ShipTypeEntry>> FetchEntriesAsync(List<int> typeIds, CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsJsonAsync($"{BaseUrl}/universe/names/?datasource=tranquility", typeIds, ct);
            if (!response.IsSuccessStatusCode) return new List<ShipTypeEntry>();

            var json = await response.Content.ReadAsStringAsync(ct);
            var root = JsonDocument.Parse(json).RootElement;
            var entries = new List<ShipTypeEntry>();
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in root.EnumerateArray())
                {
                    if (entry.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name
                        && entry.TryGetProperty("id", out var idEl))
                    {
                        entries.Add(new ShipTypeEntry(idEl.GetInt32(), name));
                    }
                }
            }
            return entries;
        }
        catch
        {
            return new List<ShipTypeEntry>();
        }
    }

    private void SaveCache(List<ShipTypeEntry> entries)
    {
        try
        {
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch
        {
            // Cache write is best-effort -- the in-memory dictionary this call returns is still valid.
        }
    }
}
