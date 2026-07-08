namespace EveDeck.Models;

// Persisted state for the Mumble "Talking UI" utility overlay (see Views/UtilityOverlayChrome).
// Unlike a SlotAssignment this isn't tied to an EVE character or layout profile -- it's a single
// free-form, global position that survives across profiles and restarts.
public sealed class UtilityOverlaySlot
{
    public bool Enabled { get; set; }
    public bool Locked { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 420;
    public int Height { get; set; } = 560;

    // Applied to both the real target window and the chrome frame (100 = fully opaque).
    // Clamped to a sane floor at apply time so a bad persisted value can't make the overlay
    // effectively invisible and ungrabbable.
    public int OpacityPercent { get; set; } = 100;

    // Captured the first time EveDeck attaches to the real window in a session, and restored when
    // detaching (including on app exit) so the user's normal Mumble window is never left borderless
    // or resized. Null = not currently attached.
    public WindowRect? OriginalRect { get; set; }
    public long OriginalStyle { get; set; }
    public long OriginalExStyle { get; set; }
}
