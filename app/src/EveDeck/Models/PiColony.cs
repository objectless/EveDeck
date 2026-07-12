using EveDeck.Utilities;

namespace EveDeck.Models;

// Health of an extractor relative to the user's alert threshold. Drives row colour in the UI.
public enum PiExtractorState { Running, ExpiringSoon, Expired, Idle }

// A single extractor control unit's live state, as shown in the Planets tab. Observable because the
// countdown text ticks on a timer between the (much rarer) ESI polls.
public sealed class PiExtractor : ObservableObject
{
    public int ProductTypeId { get; init; }
    public string ProductName { get; init; } = "";
    public DateTimeOffset? ExpiryTime { get; init; }

    // The P1 (or whatever tier) this raw resource refines into, discovered from a matching
    // single-input schematic seen somewhere in the account's colonies. Empty if not yet discovered.
    public string RefinesInto { get; init; } = "";
    public string DisplayName => string.IsNullOrEmpty(RefinesInto) ? ProductName : $"{ProductName} → {RefinesInto}";

    private string _countdownText = "";
    public string CountdownText { get => _countdownText; set => SetProperty(ref _countdownText, value); }

    private PiExtractorState _state;
    public PiExtractorState State { get => _state; set => SetProperty(ref _state, value); }

    // Recompute the human countdown + state against the given "now" and alert threshold.
    // Returns true if this extractor is inside the alert window (expiring soon or already expired),
    // so the caller can decide whether to raise a seat alert.
    public bool RefreshCountdown(DateTimeOffset now, double alertHours)
    {
        if (ExpiryTime is null)
        {
            State = PiExtractorState.Idle;
            CountdownText = "idle";
            return false;
        }

        var remaining = ExpiryTime.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            State = PiExtractorState.Expired;
            CountdownText = "expired";
            return true;
        }

        CountdownText = FormatSpan(remaining);
        var soon = remaining <= TimeSpan.FromHours(alertHours);
        State = soon ? PiExtractorState.ExpiringSoon : PiExtractorState.Running;
        return soon;
    }

    private static string FormatSpan(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m";
    }
}

// A storage-capable pin (launchpad / storage facility / command center) and how full it is.
public sealed class PiStorage
{
    public string PinName { get; init; } = "";
    public double UsedVolume { get; init; }
    public double Capacity { get; init; }
    public double FillPercent => Capacity > 0 ? Math.Min(100, UsedVolume / Capacity * 100) : 0;
}

// A Basic/Advanced/High-Tech Industry Facility pin — identified by having a schematic assigned,
// distinct from a plain storage pin. Shows what it's actually producing (any tier, P1 through P4),
// resolved from the real ESI schematic rather than a hardcoded recipe table.
public sealed class PiFactory
{
    public string PinName { get; init; } = "";
    public string OutputName { get; init; } = "";
    public IReadOnlyList<string> InputNames { get; init; } = Array.Empty<string>();
    public double UsedVolume { get; init; }
    public double Capacity { get; init; }
    public double FillPercent => Capacity > 0 ? Math.Min(100, UsedVolume / Capacity * 100) : 0;

    public bool HasRecipe => !string.IsNullOrEmpty(OutputName);
    public string RecipeText => HasRecipe
        ? $"producing {OutputName} from {string.Join(", ", InputNames)}"
        : "no schematic set";
}

// One colony (planet) for one character. Rebuilt wholesale each ESI poll.
public sealed class PiColony : ObservableObject
{
    public long CharacterId { get; init; }
    public string CharacterName { get; init; } = "";
    public int SeatNumber { get; init; }
    public int PlanetId { get; init; }
    public string PlanetType { get; init; } = "";
    public string SystemName { get; init; } = "";
    public int UpgradeLevel { get; init; }
    // The linked character's trained Interplanetary Consolidation level — null if the esi-skills
    // scope hasn't been granted yet (needs a re-link) rather than 0, so the UI can tell "unknown"
    // from "actually untrained."
    public int? InterplanetaryConsolidationLevel { get; init; }
    public int PinCount { get; init; }
    public DateTimeOffset LastUpdate { get; init; }

    public List<PiExtractor> Extractors { get; init; } = new();
    public List<PiFactory> Factories { get; init; } = new();
    public List<PiStorage> Storages { get; init; } = new();

    // Worst (fullest) buffer across the colony, storage or factory — the number the user actually
    // cares about, since a full factory input buffer stalls production same as a full launchpad.
    public double WorstFillPercent
    {
        get
        {
            var fills = Storages.Select(s => s.FillPercent).Concat(Factories.Select(f => f.FillPercent));
            return fills.DefaultIfEmpty(0).Max();
        }
    }

    // Soonest extractor expiry, for sorting colonies by urgency.
    public DateTimeOffset? NextExpiry =>
        Extractors.Where(e => e.ExpiryTime is not null).Select(e => e.ExpiryTime).DefaultIfEmpty(null).Min();

    private string _headline = "";
    public string Headline { get => _headline; set => SetProperty(ref _headline, value); }

    // Collapsed by default — with a dozen-plus colonies the full detail list gets long fast. Carried
    // forward across each poll's wholesale rebuild by MainWindowViewModel.Pi.cs so expanding a colony
    // to check something doesn't get reset out from under the user on the next refresh.
    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    public string Title => $"{SystemName} — {PlanetType}";

    public string IcLevelText => InterplanetaryConsolidationLevel is int lvl ? $" · IC L{lvl}" : "";
}

// A commodity chosen as a factory-load input (shown in the calculator's input list).
public sealed class PiFactoryInput : ObservableObject
{
    public int TypeId { get; init; }

    // True when this row was auto-discovered from the account's own colonies (via schematic
    // recipes) rather than typed in manually — auto rows get re-derived every refresh, so removing
    // one only sticks if the underlying colony stops producing it.
    public bool IsAuto { get; init; }

    private string _name = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private long _available;
    public long Available { get => _available; set { SetProperty(ref _available, value); OnPropertyChanged(nameof(AvailableText)); } }

    public string AvailableText => $"{Available:N0}";

    // Whether this material gates the split's "scarcest input" calculation. Auto-discovery can't tell
    // hauled stock (accumulates as real character assets) apart from an intermediate tier that's
    // produced and consumed entirely on-planet (e.g. a P2 chained straight into a P3 facility on the
    // same colony) — that always reads ~0 via /assets/ and would otherwise zero out the whole split.
    // Only the user knows which of their tiers are actually hauled, so this is their override.
    private bool _includeInSplit = true;
    public bool IncludeInSplit { get => _includeInSplit; set => SetProperty(ref _includeInSplit, value); }
}

// One row of the computed factory-load result (a factory and how much of each input to load).
public sealed record PiCalcRow(string Factory, long PerInputQuantity)
{
    public string PerInputText => $"{PerInputQuantity:N0}";
}

// One choice in the "PI consolidation character" picker — CharacterId null means "sum all linked
// characters" (the fallback when no consolidation character has been set).
public sealed record PiCharacterOption(long? CharacterId, string DisplayName);
