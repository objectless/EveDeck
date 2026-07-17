using EveDeck.Models;

namespace EveDeck.Utilities;

// A system name mention plus whatever words followed it up to the next mention (or end of line) --
// e.g. for "4CJ-AC Loki nv EFM-C4 clear", the two mentions are ("4CJ-AC", "Loki nv") and
// ("EFM-C4", "clear"). Trailing text is empty for a bare "system named, nothing else" mention.
public readonly record struct SystemMention(string SystemName, string TrailingText);

// What an intel report's trailing text (the words after the system name) actually said, once the
// bare fact of "this system was mentioned" isn't the whole story.
public enum IntelReportKind { Sighting, NoVisual, Clear }

// Finds EVE solar-system name mentions in freeform chat text via a sliding word-window against the
// known system-name dictionary (SystemJumpGraph), rather than pattern/regex matching -- a system
// name is just a word (or a few) as far as chat text is concerned ("hostiles in Jita"), so this is
// a dictionary lookup problem, not a grammar one. EVE system names are at most 3 words (e.g. "Old
// Man Star"), so a 3-word sliding window covers every real name.
public static class IntelSystemTokenizer
{
    private const int MaxWordsPerSystemName = 3;

    private static readonly char[] TrimChars = { ',', '.', ':', ';', '!', '?', '"', '\'', '(', ')', '[', ']', '<', '>' };

    // Returns each distinct system name mentioned in `line` that exists in `graph`, in first-seen
    // order. At each starting word, the longest candidate phrase wins (checked 3 words, then 2, then
    // 1) so "Old Man Star" matches as one system rather than "Old" plus two leftover words.
    public static List<string> FindSystemMentions(string line, SystemJumpGraph graph) =>
        FindSystemMentionsWithTrailingText(line, graph).Select(m => m.SystemName).ToList();

    // Same match as FindSystemMentions, but also captures the words between each mention and the
    // NEXT mention (or end of line) -- e.g. a ship name, "nv", or "clear" reported alongside the
    // system. Two passes: first collect every match span in order (duplicates included, so a
    // repeated mention still bounds its predecessor's trailing text correctly), then build the
    // deduplicated (first-seen-wins) result using each match's own next-match boundary.
    public static List<SystemMention> FindSystemMentionsWithTrailingText(string line, SystemJumpGraph graph)
    {
        var result = new List<SystemMention>();
        if (string.IsNullOrWhiteSpace(line) || graph.Count == 0) return result;

        var words = SplitWords(line);
        var matches = new List<(int Start, int Length, string Name)>();
        var i = 0;
        while (i < words.Count)
        {
            var matchedLength = 0;
            string? matchedName = null;
            for (var span = Math.Min(MaxWordsPerSystemName, words.Count - i); span >= 1; span--)
            {
                var candidate = string.Join(" ", words.Skip(i).Take(span));
                var node = graph.GetByName(candidate);
                if (node is not null) { matchedName = node.Name; matchedLength = span; break; }
            }
            if (matchedName is not null)
            {
                matches.Add((i, matchedLength, matchedName));
                i += matchedLength;
            }
            else i += 1;
        }

        for (var m = 0; m < matches.Count; m++)
        {
            var (start, length, name) = matches[m];
            if (result.Any(r => r.SystemName.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var trailingEnd = m + 1 < matches.Count ? matches[m + 1].Start : words.Count;
            var trailingCount = Math.Max(0, trailingEnd - (start + length));
            var trailing = string.Join(" ", words.Skip(start + length).Take(trailingCount));
            result.Add(new SystemMention(name, trailing));
        }
        return result;
    }

    // Classifies an intel mention's trailing text: a bare "nv"/"no visual" means spotted but not
    // seen on grid; "clear"/"clr" means a previously-reported system has been called safe again;
    // anything else non-empty is passed through as freeform detail (typically a ship name). Empty
    // trailing text (just the bare system name, nothing else) is a plain Sighting with no detail --
    // this is deliberately the SAME shape as every intel line built and shipped before this existed.
    public static (IntelReportKind Kind, string? Detail) ClassifyTrailingText(string trailingText)
    {
        var trimmed = trailingText.Trim();
        if (trimmed.Length == 0) return (IntelReportKind.Sighting, null);

        var lower = trimmed.ToLowerInvariant();
        if (lower is "nv" or "no visual" or "novisual") return (IntelReportKind.NoVisual, null);
        if (lower is "clear" or "clr") return (IntelReportKind.Clear, null);
        return (IntelReportKind.Sighting, trimmed);
    }

    private const int MaxWordsPerShipName = 4; // covers e.g. "Imperial Navy Slicer", "Republic Fleet Firetail"

    // Splits a Sighting's freeform detail text into a ship and whatever's left over (typically a
    // pilot name), e.g. "Ultrabug Tholos" -> (Ship: Tholos, Remainder: "Ultrabug") -- "Tholos" is a
    // known ship type, "Ultrabug" isn't. Also matches community abbreviations ("CFI" -> Cyclone
    // Fleet Issue) since ShipTypeDictionary.Resolve covers both. Unlike
    // FindSystemMentionsWithTrailingText this tries EVERY starting position, not just position 0:
    // intel reporting order varies ("pilot ship" vs "ship pilot"), and detail text is already just
    // the 1-4 words left after the system name, not a whole chat line to scan through.
    // Longest-match-first at each position for multi-word ship names. (null, phrase) when nothing in
    // the phrase matches a known ship -- the whole phrase is then presumed to be a pilot name (or
    // ship-type recognition just doesn't cover it yet).
    public static (ShipTypeEntry? Ship, string? Remainder) ResolvePilotAndShip(string phrase, ShipTypeDictionary ships)
    {
        var trimmed = phrase.Trim();
        if (trimmed.Length == 0 || ships.Count == 0) return (null, trimmed.Length == 0 ? null : trimmed);

        var words = SplitWords(trimmed);
        for (var start = 0; start < words.Count; start++)
        {
            for (var span = Math.Min(MaxWordsPerShipName, words.Count - start); span >= 1; span--)
            {
                var candidate = string.Join(" ", words.Skip(start).Take(span));
                var ship = ships.Resolve(candidate);
                if (ship is null) continue;

                var remainderWords = words.Take(start).Concat(words.Skip(start + span));
                var remainder = string.Join(" ", remainderWords);
                return (ship, remainder.Length == 0 ? null : remainder);
            }
        }
        return (null, trimmed);
    }

    // Splits on whitespace and strips chat punctuation stuck to a word's edges (commas, brackets,
    // quotes) without touching characters real system names use internally -- hyphens ("C-J6MT")
    // and digits are never trimmed, since Trim only strips characters actually in TrimChars.
    private static List<string> SplitWords(string line)
    {
        var raw = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>(raw.Length);
        foreach (var w in raw)
        {
            var trimmed = w.Trim(TrimChars);
            if (trimmed.Length > 0) words.Add(trimmed);
        }
        return words;
    }
}
