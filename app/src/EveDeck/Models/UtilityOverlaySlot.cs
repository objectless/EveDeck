namespace EveDeck.Models;

// Persisted state for the Mumble talker overlay (see Views/TalkerOverlayWindow). Unlike a
// SlotAssignment this isn't tied to an EVE character or layout profile -- it's a single
// free-form, global position that survives across profiles and restarts.
public sealed class UtilityOverlaySlot
{
    public bool Enabled { get; set; }
    public bool Locked { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    // Applied to the overlay window (100 = fully opaque). Clamped to a sane floor at apply time
    // so a bad persisted value can't make the overlay effectively invisible and ungrabbable.
    public int OpacityPercent { get; set; } = 100;

    // Uniform size scale for TalkerOverlayWindow's corner-grip resize (100 = normal size).
    public int ScalePercent { get; set; } = 100;
}
