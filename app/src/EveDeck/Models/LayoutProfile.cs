using System.Collections.ObjectModel;

namespace EveDeck.Models;

public sealed class LayoutProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Profile";
    public bool IsBuiltIn { get; set; }
    public string Category { get; set; } = "Custom";
    public ObservableCollection<LayoutSlot> Slots { get; set; } = new();

    // Family templates (Grid, Center Master) regenerate their Slots from these three fields instead of
    // storing one fixed LayoutProfile per resolution/count combo — see PresetFactory.RegenerateFamilySlots.
    public bool IsFamilyTemplate { get; set; }
    public int TemplateWidth { get; set; }
    public int TemplateHeight { get; set; }
    public int TemplateCount { get; set; }

    // Which edge the Side Stack family stacks its tiles on ("Left", "Right", "Top", or "Bottom").
    // Empty for families without a side option; PresetFactory defaults/clamps it when regenerating slots.
    public string TemplateSide { get; set; } = "";

    // The master SEAT (SlotAssignment.SlotNumber) centered at rest for THIS profile — different activities
    // (mining vs. PvP) can center different mains. 0 = unset; the view-model falls back to the center slot.
    public int MasterSeat { get; set; }

    // Swap groups partition profile slots into independent swap rings. Empty = single legacy group (all slots).
    public ObservableCollection<SwapGroup> SwapGroups { get; set; } = new();

    // When true, this profile fits itself into the monitor WORK AREA (excluding the taskbar) instead of
    // the full monitor bounds at apply time — useful for center-master grids so the bottom row clears the
    // taskbar. Per-profile so full-screen and taskbar-aware variants can coexist. See ResolveLayoutAnchor.
    public bool AvoidTaskbar { get; set; }

    // When non-zero, EveDeck places the master EVE window at exactly this size instead of whatever
    // ResolvePlacementRect computes for the center slot. Set this to match EVE's Fixed Window
    // resolution when using VSR/DSR supersampling or a custom master size.
    // 0/0 = auto (scale to fill the target monitor as usual).
    public int MasterResolutionWidth { get; set; }
    public int MasterResolutionHeight { get; set; }

    // Physical bounds of the monitor this profile was captured on.
    // Zero = legacy profile captured before this feature; no resolution scaling applied.
    public int CaptureMonitorX { get; set; }
    public int CaptureMonitorY { get; set; }
    public int CaptureMonitorWidth { get; set; }
    public int CaptureMonitorHeight { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int GroupOrder => Category switch
    {
        "Stacked"       => 0,
        "1-Char"        => 1,
        "Grid"          => 2,
        "Center Master" => 3,
        "Whammy Board"  => 4,
        "Side Stack"    => 5,
        "Twin Stack"    => 6,
        "Overlap"       => 7,
        _               => 99
    };

    // Corner-overlay (grid) mode needs distinct slot positions to surround a center rect. Single-client
    // and stacked layouts place every client at the same spot, so they "won't grid" — for those we fall
    // back to plain window placement even when corner overlays are globally enabled.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SupportsCornerGrid =>
        Slots.Count >= 2 && Slots.Select(s => (s.X, s.Y)).Distinct().Count() >= 2;

    // WPF ComboBox selection boxes fall back to ToString() in some template paths even with
    // DisplayMemberPath set - show the profile name instead of the type name.
    public override string ToString() => Name;

    public LayoutProfile Clone(string? name = null)
    {
        var clone = new LayoutProfile { Name = name ?? $"{Name} Copy", IsBuiltIn = false, Category = "Custom", IsFamilyTemplate = false };
        clone.MasterSeat = MasterSeat;
        clone.AvoidTaskbar = AvoidTaskbar;
        clone.CaptureMonitorX = CaptureMonitorX;
        clone.CaptureMonitorY = CaptureMonitorY;
        clone.CaptureMonitorWidth = CaptureMonitorWidth;
        clone.CaptureMonitorHeight = CaptureMonitorHeight;
        foreach (var slot in Slots)
        {
            clone.Slots.Add(new LayoutSlot
            {
                SlotNumber = slot.SlotNumber,
                Label = slot.Label,
                MonitorId = slot.MonitorId,
                X = slot.X,
                Y = slot.Y,
                Width = slot.Width,
                Height = slot.Height,
                Borderless = slot.Borderless,
                HomeSeat = slot.HomeSeat
            });
        }
        foreach (var g in SwapGroups)
            clone.SwapGroups.Add(new SwapGroup { Name = g.Name, SlotNumbers = new List<int>(g.SlotNumbers) });
        return clone;
    }
}
