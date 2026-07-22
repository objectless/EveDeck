using EveDeck.Utilities;

namespace EveDeck.Models;

// A named bundle that switches your whole setup at once -- "Mining" vs "PvP" as a single choice.
//
// EveDeck already had two partial notions of a profile, and this deliberately does NOT become a
// third overlapping one. Instead it BINDS them and owns the one bucket neither covered:
//
//   LayoutProfileId  -> references an existing LayoutProfile  (where windows go: slot rects,
//                       master seat, swap groups)
//   CharacterSetId   -> references an existing CharacterSet   (who sits where + the hotkey bindings)
//   Appearance       -> OWNS a snapshot of the overlay look-and-feel settings, which live flat on
//                       AppSettings and previously could not be varied at all
//
// The first two are REFERENCES, not copies (a deliberate choice): two config profiles can point at
// the same layout, so tweaking that layout once updates both instead of drifting into stale
// duplicates. The cost is dangling references when the target is deleted -- see
// ConfigProfileService.Apply, which degrades to "leave that part alone" rather than throwing.
public sealed class ConfigProfile : ObservableObject
{
    private string _name = "New Config";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    // Empty = "don't switch this part" rather than "no layout". A config profile that only changes
    // the overlay look is a legitimate and useful thing to have.
    public string LayoutProfileId { get; set; } = "";
    public string CharacterSetId { get; set; } = "";

    // AppSettings property name -> that property's value as a JSON fragment. A name/value bag rather
    // than a parallel class with ~40 mirrored fields: the whitelist of which settings belong here
    // lives in ConfigProfileService, so adding a new appearance setting is a one-line change there
    // instead of a new field in two places that can silently drift apart.
    public Dictionary<string, string> Appearance { get; set; } = new();

    public override string ToString() => Name;
}
