using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using EveDeck.Models;

namespace EveDeck.Services;

// Crawls ESI's PUBLIC universe endpoints (no auth) to build the full solar-system stargate graph
// (~8000 systems), then caches it to disk forever -- gate topology never changes at runtime. This is
// infrastructure for "how many jumps away is system X" lookups; see SystemJumpGraph for the actual
// BFS. Mirrors EsiTypeCache's cache-forever-on-disk convention, but for one big graph blob instead of
// many small per-id facts.
public sealed class SystemJumpGraphService
{
    private const string BaseUrl = "https://esi.evetech.net/latest";
    private const int MaxConcurrency = 24;

    private static readonly HttpClient _http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _cachePath;

    public SystemJumpGraphService(string appDataFolder)
    {
        var dir = Path.Combine(appDataFolder, "cache");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "system-jump-graph.json");
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EveDeck/Intel (github.com/objectless/EveDeck)");
        return http;
    }

    // Loads the on-disk cache without touching the network. Null if missing/corrupt/empty -- caller
    // should fall back to a bundled seed file (see TryLoadFrom) or ultimately BuildAsync.
    public SystemJumpGraph? TryLoadCached() => TryLoadFrom(_cachePath);

    // Loads a system graph from an arbitrary path, same JSON shape as the cache file -- used to load
    // the seed file bundled with the app (see MainWindowViewModel.IntelJumpAlert.cs) without
    // duplicating the parsing logic. Null if missing/corrupt/empty.
    public static SystemJumpGraph? TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var nodes = JsonSerializer.Deserialize<List<SystemNode>>(File.ReadAllText(path), JsonOptions);
            if (nodes is null || nodes.Count == 0) return null;
            return new SystemJumpGraph(nodes);
        }
        catch
        {
            // Corrupt/partial cache file -- caller rebuilds via BuildAsync.
            return null;
        }
    }

    // Full one-time crawl: all system IDs -> each system's stargates -> each stargate's destination
    // system. Safe to call again later to refresh -- it just overwrites the cache file. Bounded
    // 24-wide parallelism across ~8000 systems plus every stargate seen; serially this would take far
    // too long. A single system/stargate fetch failing never aborts the crawl -- see the per-fetch
    // helpers below, which always return a usable fallback instead of throwing.
    //
    // Progress is reported in two phases (system fetch, then stargate-destination fetch), each with
    // its own done/total -- callers driving a single progress bar should expect `total` to reset once
    // partway through.
    public async Task<SystemJumpGraph> BuildAsync(IProgress<(int done, int total)>? progress, CancellationToken ct)
    {
        var systemIds = await FetchSystemIdsAsync(ct);
        var systemTotal = systemIds.Count;
        var systemDone = 0;

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct };
        var systemInfo = new ConcurrentDictionary<int, (string Name, List<int> StargateIds)>();

        await Parallel.ForEachAsync(systemIds, parallelOptions, async (id, token) =>
        {
            systemInfo[id] = await FetchSystemDetailAsync(id, token);
            var done = Interlocked.Increment(ref systemDone);
            if (done % 50 == 0 || done == systemTotal) progress?.Report((done, systemTotal));
        });

        // Wormhole systems contribute no stargate ids at all -- SelectMany just skips them.
        var stargateIds = systemInfo.Values.SelectMany(v => v.StargateIds).Distinct().ToList();
        var stargateTotal = stargateIds.Count;
        var stargateDone = 0;
        var destinations = new ConcurrentDictionary<int, int>();

        await Parallel.ForEachAsync(stargateIds, parallelOptions, async (gateId, token) =>
        {
            var destSystemId = await FetchStargateDestinationAsync(gateId, token);
            if (destSystemId is int resolved) destinations[gateId] = resolved;
            var done = Interlocked.Increment(ref stargateDone);
            if (done % 50 == 0 || done == stargateTotal) progress?.Report((done, stargateTotal));
        });

        var nodes = new List<SystemNode>(systemInfo.Count);
        foreach (var (id, info) in systemInfo)
        {
            var neighborIds = new List<int>(info.StargateIds.Count);
            foreach (var gateId in info.StargateIds)
            {
                // Skip any stargate whose destination lookup failed rather than aborting the crawl.
                if (destinations.TryGetValue(gateId, out var destId)) neighborIds.Add(destId);
            }
            nodes.Add(new SystemNode { Id = id, Name = info.Name, NeighborIds = neighborIds });
        }

        SaveCache(nodes);
        return new SystemJumpGraph(nodes);
    }

    private async Task<List<int>> FetchSystemIdsAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/universe/systems/", ct);
            return JsonSerializer.Deserialize<List<int>>(json) ?? new List<int>();
        }
        catch
        {
            return new List<int>();
        }
    }

    private async Task<(string Name, List<int> StargateIds)> FetchSystemDetailAsync(int systemId, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/universe/systems/{systemId}/", ct);
            var root = JsonDocument.Parse(json).RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? $"System {systemId}" : $"System {systemId}";
            var stargateIds = new List<int>();
            // Wormhole systems have no "stargates" property at all -- treat as empty, not an error.
            if (root.TryGetProperty("stargates", out var gates) && gates.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gates.EnumerateArray())
                {
                    if (g.ValueKind == JsonValueKind.Number) stargateIds.Add(g.GetInt32());
                }
            }
            return (name, stargateIds);
        }
        catch
        {
            // Keep the system in the graph (by id) even if its detail fetch failed -- just with no
            // known neighbors yet. A later refresh can fill it in.
            return ($"System {systemId}", new List<int>());
        }
    }

    private async Task<int?> FetchStargateDestinationAsync(int stargateId, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/universe/stargates/{stargateId}/", ct);
            var root = JsonDocument.Parse(json).RootElement;
            if (root.TryGetProperty("destination", out var dest) && dest.TryGetProperty("system_id", out var sysId))
            {
                return sysId.GetInt32();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void SaveCache(List<SystemNode> nodes)
    {
        try
        {
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(nodes, JsonOptions));
        }
        catch
        {
            // Cache write is best-effort -- the in-memory graph this call returns is still valid.
        }
    }
}
