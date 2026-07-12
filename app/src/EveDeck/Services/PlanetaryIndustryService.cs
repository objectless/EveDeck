using EveDeck.Models;

namespace EveDeck.Services;

// Orchestrates the read-only PI monitor: pulls every linked character's colonies from ESI, resolves
// type/system names + storage capacities + factory recipes (any PI tier), and shapes them into
// PiColony view models. Also totals commodity stock across characters for the factory-load calculator,
// and reads each character's Interplanetary Consolidation skill level.
//
// Purely read (GET /planets, GET /assets, GET /skills); nothing here changes game state, keeping it
// well inside EveDeck's window-manager-only EULA boundary.
public sealed class PlanetaryIndustryService
{
    private const string InterplanetaryConsolidationSkillName = "Interplanetary Consolidation";

    private readonly EsiClient _client;
    private readonly EsiTypeCache _types;

    public PlanetaryIndustryService(EsiClient client, EsiTypeCache types)
    {
        _client = client;
        _types = types;
    }

    // Fetch colonies for the given (characterId, seatNumber, name) tuples. Failures for one character
    // are logged via onError and skipped, so one un-relinked alt doesn't blank the whole panel.
    //
    // Two-phase: first pull every colony's raw detail, then derive every industry-facility pin's
    // recipe from its routes (see BuildColonyAsync doc for why routes, not the schematics endpoint),
    // and only THEN build the colony view models — so an extractor in the first colony fetched can
    // still show "-> P1" even though the matching Basic Industry Facility lives in the fiftieth
    // colony fetched.
    public async Task<List<PiColony>> FetchColoniesAsync(
        IEnumerable<(long CharacterId, int SeatNumber, string Name)> characters,
        Action<string>? onError,
        CancellationToken ct)
    {
        var characterList = characters.ToList();
        var rows = new List<(long CharId, int Seat, string Name, EsiPlanetSummary Summary, EsiPlanetDetail Detail, string SystemName)>();

        foreach (var (charId, seat, name) in characterList)
        {
            List<EsiPlanetSummary>? summaries;
            try
            {
                summaries = await _client.GetAsync<List<EsiPlanetSummary>>(
                    $"/characters/{charId}/planets/", charId, ct);
            }
            catch (EsiAuthException ex)
            {
                onError?.Invoke(ex.Message);
                continue;
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Failed to load colonies for {name}: {ex.Message}");
                continue;
            }

            if (summaries is null) continue;

            foreach (var s in summaries)
            {
                try
                {
                    var detail = await _client.GetAsync<EsiPlanetDetail>(
                        $"/characters/{charId}/planets/{s.PlanetId}/", charId, ct);
                    if (detail is null) continue;
                    var systemName = await _types.GetSystemNameAsync(s.SolarSystemId, ct);
                    rows.Add((charId, seat, name, s, detail, systemName));
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"Failed to load planet {s.PlanetId} for {name}: {ex.Message}");
                }
            }
        }

        foreach (var row in rows)
        {
            foreach (var pin in row.Detail.Pins)
            {
                if (pin.SchematicId is not int schematicId) continue;
                var outputName = await _types.GetSchematicNameAsync(schematicId, ct);
                if (string.IsNullOrEmpty(outputName)) continue;

                var inputIds = FactoryInputTypeIds(row.Detail, pin.PinId);
                if (inputIds.Count == 1)
                {
                    // Basic Industry Facility: single P0 input -> P1 output. The P0 itself must NOT go
                    // into the calculator's input set — it's raw material still sitting on the
                    // extractor planet, not hauled stock, and character /assets/ can return a nonzero
                    // total for it anyway (e.g. sitting in that planet's own launchpad), which as the
                    // SCARCEST input would zero out the whole split. Only its refined P1 (registered
                    // below, when some Advanced facility elsewhere consumes it) belongs in the calc.
                    _types.RegisterP0Refinement(inputIds[0], outputName);
                }
                else if (inputIds.Count > 1)
                {
                    // Advanced/High-Tech recipe: every ingredient (P1, P2, ...) is real hauled stock a
                    // factory consumes — worth totalling and splitting.
                    foreach (var id in inputIds) _types.RegisterFactoryInputTypeId(id);
                }
            }
        }

        var icLevels = await FetchInterplanetaryConsolidationLevelsAsync(
            rows.Select(r => r.CharId).Distinct(), onError, ct);

        var colonies = new List<PiColony>();
        foreach (var row in rows)
        {
            try
            {
                var icLevel = icLevels.TryGetValue(row.CharId, out var lvl) ? lvl : (int?)null;
                colonies.Add(await BuildColonyAsync(row.CharId, row.Seat, row.Name, row.Summary, row.Detail, row.SystemName, icLevel, ct));
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Failed to process planet {row.Summary.PlanetId} for {row.Name}: {ex.Message}");
            }
        }

        return colonies;
    }

    // The type ids actually routed INTO a pin, per that colony's own routes[] array — this is the
    // ground truth for "what does this factory consume", since ESI's /universe/schematics/ endpoint
    // only exposes a schematic's name, not its recipe composition. Works regardless of whether
    // material reaches the factory directly from an extractor or via an intermediate launchpad, since
    // it only looks at the pin's own inbound routes, not the whole chain.
    private static List<int> FactoryInputTypeIds(EsiPlanetDetail detail, long pinId)
        => detail.Routes.Where(r => r.DestinationPinId == pinId).Select(r => r.ContentTypeId).Distinct().ToList();

    private async Task<PiColony> BuildColonyAsync(
        long charId, int seat, string name, EsiPlanetSummary summary, EsiPlanetDetail detail, string systemName,
        int? icLevel, CancellationToken ct)
    {
        var extractors = new List<PiExtractor>();
        var factories = new List<PiFactory>();
        var storages = new List<PiStorage>();

        foreach (var pin in detail.Pins)
        {
            if (pin.Extractor is not null)
            {
                var productId = pin.Extractor.ProductTypeId ?? 0;
                var productName = productId > 0 ? (await _types.GetTypeAsync(productId, ct)).Name : "—";
                extractors.Add(new PiExtractor
                {
                    ProductTypeId = productId,
                    ProductName = productName,
                    RefinesInto = productId > 0 ? _types.GetSingleInputProduct(productId) : "",
                    ExpiryTime = pin.ExpiryTime,
                });
            }
            else if (pin.SchematicId is int schematicId)
            {
                // A Basic/Advanced/High-Tech Industry Facility — identified by having a schematic
                // assigned, at any PI tier. Labeled with its real recipe (output from the schematic's
                // name, inputs from this colony's own routes — see FactoryInputTypeIds).
                var pinType = await _types.GetTypeAsync(pin.TypeId, ct);
                var outputName = await _types.GetSchematicNameAsync(schematicId, ct); // cached from the pre-pass

                double used = 0;
                if (pin.Contents is not null)
                    foreach (var c in pin.Contents)
                        used += c.Amount * (await _types.GetTypeAsync(c.TypeId, ct)).Volume;

                var inputNames = new List<string>();
                foreach (var inputId in FactoryInputTypeIds(detail, pin.PinId))
                    inputNames.Add((await _types.GetTypeAsync(inputId, ct)).Name);

                factories.Add(new PiFactory
                {
                    PinName = pinType.Name,
                    OutputName = outputName,
                    InputNames = inputNames,
                    UsedVolume = used,
                    Capacity = pinType.Capacity,
                });
            }
            else if (pin.Contents is not null)
            {
                var pinType = await _types.GetTypeAsync(pin.TypeId, ct);
                if (pinType.Capacity <= 0) continue; // not a storage-capable pin

                double used = 0;
                foreach (var c in pin.Contents)
                {
                    var ct2 = await _types.GetTypeAsync(c.TypeId, ct);
                    used += c.Amount * ct2.Volume;
                }

                storages.Add(new PiStorage
                {
                    PinName = pinType.Name,
                    UsedVolume = used,
                    Capacity = pinType.Capacity,
                });
            }
        }

        return new PiColony
        {
            CharacterId = charId,
            CharacterName = name,
            SeatNumber = seat,
            PlanetId = summary.PlanetId,
            PlanetType = Capitalize(summary.PlanetType),
            SystemName = systemName,
            UpgradeLevel = summary.UpgradeLevel,
            InterplanetaryConsolidationLevel = icLevel,
            PinCount = summary.NumPins,
            LastUpdate = summary.LastUpdate,
            Extractors = extractors,
            Factories = factories,
            Storages = storages,
        };
    }

    // Reads each character's trained Interplanetary Consolidation skill level (requires the
    // esi-skills.read_skills.v1 scope — characters linked before it was added won't resolve until
    // re-authed). The skill's type id is resolved once via ESI's public name-search rather than
    // hardcoded, then cached forever (EsiTypeCache).
    private async Task<Dictionary<long, int>> FetchInterplanetaryConsolidationLevelsAsync(
        IEnumerable<long> characterIds, Action<string>? onError, CancellationToken ct)
    {
        var result = new Dictionary<long, int>();
        var skillTypeId = await _types.ResolveTypeIdByNameAsync(InterplanetaryConsolidationSkillName, ct);
        if (skillTypeId <= 0) return result; // resolution failed; callers just show no IC level

        foreach (var charId in characterIds)
        {
            try
            {
                var skills = await _client.GetAsync<EsiCharacterSkillsResponse>($"/characters/{charId}/skills/", charId, ct);
                var level = skills?.Skills.FirstOrDefault(s => s.SkillId == skillTypeId)?.TrainedSkillLevel;
                if (level is not null) result[charId] = level.Value;
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Failed to load skills for character {charId}: {ex.Message}");
            }
        }

        return result;
    }

    // Totals stock of each requested type id across the given characters (for the factory-load calc).
    public async Task<Dictionary<int, long>> TotalStockAsync(
        IEnumerable<long> characterIds, IReadOnlyCollection<int> typeIds,
        Action<string>? onError, CancellationToken ct)
    {
        var totals = typeIds.ToDictionary(id => id, _ => 0L);
        if (typeIds.Count == 0) return totals;
        var wanted = typeIds.ToHashSet();

        foreach (var charId in characterIds)
        {
            List<EsiAssetItem> assets;
            try
            {
                assets = await _client.GetPagedAsync<EsiAssetItem>($"/characters/{charId}/assets/", charId, ct);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Failed to load assets for character {charId}: {ex.Message}");
                continue;
            }

            foreach (var a in assets)
                if (wanted.Contains(a.TypeId))
                    totals[a.TypeId] += a.Quantity;
        }

        return totals;
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
