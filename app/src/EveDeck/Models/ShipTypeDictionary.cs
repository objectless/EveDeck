namespace EveDeck.Models;

// One real, currently-flyable EVE ship type -- both fields are needed downstream: Name for text
// matching, Id to fetch the ship's icon (see ShipIconCacheService).
public readonly record struct ShipTypeEntry(int Id, string Name);

// Pure in-memory ship-type lookup: no network/file I/O, trivially unit-testable. Built once (see
// ShipTypeDictionaryService) from ESI's public ship category and queried repeatedly to tell a ship
// name apart from a pilot name in intel trailing text (e.g. "Ultrabug Tholos" -- "Tholos" is a ship,
// "Ultrabug" isn't). Unlike SystemJumpGraph there is no graph/BFS here -- ships don't have a
// "distance", this is just a name (and community-abbreviation) lookup.
public sealed class ShipTypeDictionary
{
    private readonly Dictionary<string, ShipTypeEntry> _byName;
    private readonly Dictionary<string, ShipTypeEntry> _byAcronym;

    public ShipTypeDictionary(IEnumerable<ShipTypeEntry> entries)
    {
        _byName = new Dictionary<string, ShipTypeEntry>(StringComparer.OrdinalIgnoreCase);
        var acronymCandidates = new Dictionary<string, List<ShipTypeEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            _byName[entry.Name] = entry;

            // Community-standard abbreviation for a multi-word faction hull: first letter of each
            // word, e.g. "Cyclone Fleet Issue" -> CFI, "Ferox Navy Issue" -> FNI. Single-word names
            // (most T1 hulls) don't get one -- there's nothing to abbreviate.
            var acronym = Acronym(entry.Name);
            if (acronym is null) continue;
            if (!acronymCandidates.TryGetValue(acronym, out var list))
                acronymCandidates[acronym] = list = new List<ShipTypeEntry>();
            list.Add(entry);
        }

        // Only keep an acronym when exactly one ship reduces to it -- a collision (two different
        // hulls sharing the same initials) is skipped entirely rather than guessing which one a
        // caller meant. Better to miss an abbreviation than resolve it to the wrong ship.
        _byAcronym = new Dictionary<string, ShipTypeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (acronym, candidates) in acronymCandidates)
        {
            if (candidates.Count == 1) _byAcronym[acronym] = candidates[0];
        }
    }

    public int Count => _byName.Count;

    // Every known ship's type id -- used to bulk-precache icons (see ShipIconCacheService) once the
    // dictionary itself is available, regardless of whether it came from a live crawl or a bundled
    // seed file.
    public IEnumerable<int> AllIds => _byName.Values.Select(e => e.Id);

    // Matches a full ship name OR a generated abbreviation (any casing) -- null if `name` is neither.
    public ShipTypeEntry? Resolve(string name)
    {
        if (_byName.TryGetValue(name, out var byName)) return byName;
        if (_byAcronym.TryGetValue(name, out var byAcronym)) return byAcronym;
        return null;
    }

    private static string? Acronym(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;
        return string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
    }
}
