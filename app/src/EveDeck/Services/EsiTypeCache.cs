using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace EveDeck.Services;

// Resolves EVE type/system/schematic facts via the PUBLIC ESI universe endpoints (no auth). These
// facts are immutable, so results are cached forever on disk — after the first sight of a type the
// app never asks ESI about it again. Missing/failed lookups fall back to a synthetic "Type 1234"
// name so the UI degrades gracefully rather than throwing.
//
// NOTE: ESI's /universe/schematics/{id}/ endpoint only returns a schematic's NAME and cycle time —
// it does NOT expose recipe composition (inputs/outputs), despite that being a reasonable-looking
// guess from the endpoint's name. There is no public ESI endpoint for PI recipe composition at all.
// So "what feeds this factory" is derived instead from the account's own colony data: a planet's
// routes[] array reports exactly which type_id flows into which pin_id, which combined with the
// schematic's name (the output) gives the real recipe — grounded in the user's own colonies, not a
// hardcoded table. See PlanetaryIndustryService for where that derivation happens.
public sealed class EsiTypeCache
{
    private const string BaseUrl = "https://esi.evetech.net/latest";

    private static readonly HttpClient _http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _typePath;
    private readonly string _systemPath;
    private readonly string _schematicPath;
    private readonly string _nameIdPath;
    private readonly ConcurrentDictionary<int, EsiTypeInfo> _types = new();
    private readonly ConcurrentDictionary<int, string> _systems = new();
    private readonly ConcurrentDictionary<int, string> _schematicNames = new();
    private readonly ConcurrentDictionary<string, int> _typeIdByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, Task<EsiTypeInfo>> _typeInflight = new();
    private readonly ConcurrentDictionary<int, Task<string>> _systemInflight = new();
    private readonly ConcurrentDictionary<int, Task<string>> _schematicInflight = new();

    // Derived from the account's own colony routing (see PlanetaryIndustryService): for a raw resource
    // fed into a factory pin whose OTHER inputs are none (a Basic Industry Facility, single input), the
    // raw resource's type id maps to the refined commodity's name — e.g. Reactive Gas -> "Oxidizing
    // Compound". Lets an extractor be labeled with what it refines into.
    private readonly ConcurrentDictionary<int, string> _singleInputProduct = new();
    // Every commodity known to feed SOME factory pin in the account's colonies, at any tier (P1s
    // feeding an Advanced facility, P2s feeding a High-Tech one, etc — but never bare P0, which never
    // leaves the originating planet so totalling it via character assets wouldn't mean anything).
    // Used to auto-populate the factory-load calculator's inputs.
    private readonly ConcurrentDictionary<int, byte> _factoryInputTypeIds = new();

    private readonly object _saveLock = new();

    public EsiTypeCache(string appDataFolder)
    {
        var dir = Path.Combine(appDataFolder, "cache");
        Directory.CreateDirectory(dir);
        _typePath = Path.Combine(dir, "esi-types.json");
        _systemPath = Path.Combine(dir, "esi-systems.json");
        _schematicPath = Path.Combine(dir, "esi-schematic-names.json");
        _nameIdPath = Path.Combine(dir, "esi-name-ids.json");
        LoadTypes();
        LoadSystems();
        LoadSchematicNames();
        LoadNameIds();
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EveDeck/PI (github.com/objectless/EveDeck)");
        return http;
    }

    public async Task<EsiTypeInfo> GetTypeAsync(int typeId, CancellationToken ct)
    {
        if (typeId <= 0) return EsiTypeInfo.Unknown(typeId);
        if (_types.TryGetValue(typeId, out var cached)) return cached;
        return await _typeInflight.GetOrAdd(typeId, id => FetchTypeAsync(id, ct));
    }

    public async Task<string> GetSystemNameAsync(int systemId, CancellationToken ct)
    {
        if (systemId <= 0) return $"System {systemId}";
        if (_systems.TryGetValue(systemId, out var name)) return name;
        return await _systemInflight.GetOrAdd(systemId, id => FetchSystemAsync(id, ct));
    }

    // The schematic's name — for PI schematics this IS the product name (e.g. schematic "Water"
    // produces Water). That's the only composition fact ESI's schematics endpoint actually exposes.
    public async Task<string> GetSchematicNameAsync(int schematicId, CancellationToken ct)
    {
        if (schematicId <= 0) return "";
        if (_schematicNames.TryGetValue(schematicId, out var name)) return name;
        return await _schematicInflight.GetOrAdd(schematicId, id => FetchSchematicNameAsync(id, ct));
    }

    // Resolves an exact item/skill name to its type id via POST /universe/ids/ (public, no auth).
    // Used to find e.g. "Interplanetary Consolidation"'s skill_id without hardcoding it. 0 if not found.
    public async Task<int> ResolveTypeIdByNameAsync(string name, CancellationToken ct)
    {
        if (_typeIdByName.TryGetValue(name, out var id)) return id;
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(new[] { name }), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BaseUrl}/universe/ids/", body, ct);
            resp.EnsureSuccessStatusCode();
            var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
            if (root.TryGetProperty("inventory_types", out var types) && types.GetArrayLength() > 0)
            {
                var resolved = types[0].GetProperty("id").GetInt32();
                _typeIdByName[name] = resolved;
                SaveNameIds();
                return resolved;
            }
        }
        catch { /* next caller just retries — nothing cached on failure */ }
        return 0;
    }

    // Learned from the account's own colony routing (PlanetaryIndustryService) — see class doc.
    public void RegisterP0Refinement(int p0TypeId, string p1Name)
    {
        if (p0TypeId <= 0 || string.IsNullOrEmpty(p1Name)) return;
        if (_singleInputProduct.TryGetValue(p0TypeId, out var existing) && existing == p1Name) return;
        _singleInputProduct[p0TypeId] = p1Name;
    }

    public void RegisterFactoryInputTypeId(int typeId)
    {
        if (typeId > 0) _factoryInputTypeIds[typeId] = 1;
    }

    // "What does this raw resource refine into?" — empty string if not yet discovered.
    public string GetSingleInputProduct(int inputTypeId)
        => _singleInputProduct.TryGetValue(inputTypeId, out var name) ? name : "";

    // Every commodity known to feed some factory pin in the account's colonies (any tier). Used to
    // auto-populate the factory-load calculator's inputs.
    public IReadOnlyCollection<int> GetDiscoveredFactoryInputTypeIds() => _factoryInputTypeIds.Keys.ToList();

    private async Task<EsiTypeInfo> FetchTypeAsync(int typeId, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/universe/types/{typeId}/", ct);
            var root = JsonDocument.Parse(json).RootElement;
            var info = new EsiTypeInfo
            {
                TypeId = typeId,
                Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? $"Type {typeId}" : $"Type {typeId}",
                Volume = root.TryGetProperty("volume", out var v) ? v.GetDouble() : 0,
                Capacity = root.TryGetProperty("capacity", out var c) ? c.GetDouble() : 0,
                GroupId = root.TryGetProperty("group_id", out var g) ? g.GetInt32() : 0,
            };
            _types[typeId] = info;
            SaveTypes();
            return info;
        }
        catch
        {
            // Don't poison the permanent cache with a failed lookup; return a transient placeholder so
            // a later tick can retry.
            return EsiTypeInfo.Unknown(typeId);
        }
        finally
        {
            _typeInflight.TryRemove(typeId, out _);
        }
    }

    private async Task<string> FetchSystemAsync(int systemId, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/universe/systems/{systemId}/", ct);
            var root = JsonDocument.Parse(json).RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? $"System {systemId}" : $"System {systemId}";
            _systems[systemId] = name;
            SaveSystems();
            return name;
        }
        catch
        {
            return $"System {systemId}";
        }
        finally
        {
            _systemInflight.TryRemove(systemId, out _);
        }
    }

    private async Task<string> FetchSchematicNameAsync(int schematicId, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/universe/schematics/{schematicId}/", ct);
            var root = JsonDocument.Parse(json).RootElement;
            var name = root.TryGetProperty("schematic_name", out var n) ? n.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(name))
            {
                _schematicNames[schematicId] = name;
                SaveSchematicNames();
            }
            return name;
        }
        catch
        {
            // Don't poison the cache with a failed lookup; the next refresh will just retry.
            return "";
        }
        finally
        {
            _schematicInflight.TryRemove(schematicId, out _);
        }
    }

    private void LoadTypes()
    {
        try
        {
            if (!File.Exists(_typePath)) return;
            var list = JsonSerializer.Deserialize<List<EsiTypeInfo>>(File.ReadAllText(_typePath), JsonOptions);
            if (list is null) return;
            foreach (var t in list) _types[t.TypeId] = t;
        }
        catch { /* stale/corrupt cache is harmless — it just gets rebuilt from ESI */ }
    }

    private void LoadSystems()
    {
        try
        {
            if (!File.Exists(_systemPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(_systemPath), JsonOptions);
            if (map is null) return;
            foreach (var kv in map) _systems[kv.Key] = kv.Value;
        }
        catch { /* stale/corrupt cache is harmless */ }
    }

    private void LoadSchematicNames()
    {
        try
        {
            if (!File.Exists(_schematicPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(_schematicPath), JsonOptions);
            if (map is null) return;
            foreach (var kv in map) _schematicNames[kv.Key] = kv.Value;
        }
        catch { /* stale/corrupt cache is harmless — it just gets rebuilt from ESI */ }
    }

    private void LoadNameIds()
    {
        try
        {
            if (!File.Exists(_nameIdPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_nameIdPath), JsonOptions);
            if (map is null) return;
            foreach (var kv in map) _typeIdByName[kv.Key] = kv.Value;
        }
        catch { /* stale/corrupt cache is harmless */ }
    }

    private void SaveTypes()
    {
        lock (_saveLock)
        {
            try { File.WriteAllText(_typePath, JsonSerializer.Serialize(_types.Values.ToList(), JsonOptions)); }
            catch { /* cache write is best-effort */ }
        }
    }

    private void SaveSystems()
    {
        lock (_saveLock)
        {
            try { File.WriteAllText(_systemPath, JsonSerializer.Serialize(_systems.ToDictionary(k => k.Key, v => v.Value), JsonOptions)); }
            catch { /* cache write is best-effort */ }
        }
    }

    private void SaveSchematicNames()
    {
        lock (_saveLock)
        {
            try { File.WriteAllText(_schematicPath, JsonSerializer.Serialize(_schematicNames.ToDictionary(k => k.Key, v => v.Value), JsonOptions)); }
            catch { /* cache write is best-effort */ }
        }
    }

    private void SaveNameIds()
    {
        lock (_saveLock)
        {
            try { File.WriteAllText(_nameIdPath, JsonSerializer.Serialize(_typeIdByName.ToDictionary(k => k.Key, v => v.Value), JsonOptions)); }
            catch { /* cache write is best-effort */ }
        }
    }
}

public sealed class EsiTypeInfo
{
    public int TypeId { get; set; }
    public string Name { get; set; } = "";
    public double Volume { get; set; }     // m³ per unit
    public double Capacity { get; set; }   // m³ storage capacity (structures only; 0 for commodities)
    public int GroupId { get; set; }

    public static EsiTypeInfo Unknown(int typeId) => new() { TypeId = typeId, Name = $"Type {typeId}" };
}
