using System.Collections.ObjectModel;

namespace EveDeck.Models;

public sealed class AppSettings
{
    public bool UsePhysicalPixels { get; set; } = true;
    public string LayoutTargetMonitorId { get; set; } = "";
    public bool UseMonitorWorkArea { get; set; }
    public bool IncludeNotepadTestWindows { get; set; } = true;
    public bool AutoRefresh { get; set; } = true;
    public string ActiveProfileId { get; set; } = "";
    public ObservableCollection<SlotAssignment> Assignments { get; set; } = new();
    public ObservableCollection<LayoutProfile> Profiles { get; set; } = new();
    public ObservableCollection<HotkeyBinding> Hotkeys { get; set; } = new();
    public ObservableCollection<CharacterSet> CharacterSets { get; set; } = new();
    public string ActiveCharacterSetId { get; set; } = "";
    public Dictionary<string, StyleSnapshot> StyleSnapshotsByTitle { get; set; } = new();

    public bool ActiveFrameEnabled { get; set; } = true;
    public int ActiveFrameThickness { get; set; } = 4;
    public int ActiveFrameGlowRadius { get; set; } = 8;
    public string ActiveFrameColor { get; set; } = "#F59E0B";

    // 2g — Startup profile auto-apply
    public bool ApplyProfileOnStartup { get; set; }
    public string StartupProfileId { get; set; } = "";

    // Re-apply the active profile automatically when assigned EVE clients (re)appear — e.g. after
    // closing all clients and launching them again, without clicking Apply manually.
    public bool AutoApplyOnClientLaunch { get; set; } = true;

    // 2a — Minimize to tray
    public bool MinimizeToTray { get; set; } = true;

    // UI scale (1.0 = 100%, applied as LayoutTransform on the main window content)
    public double UiScale { get; set; } = 1.0;

    // Master slot for the swap-focused-with-master hotkey action.
    public int MasterSlotNumber { get; set; } = 1;

    // Corner overlay mode: all clients run at master resolution; corners show DWM thumbnails.
    // On by default — this is the primary grid experience. Profiles that can't form a grid
    // (single-client and stacked layouts) automatically fall back to plain window placement
    // (see LayoutProfile.SupportsCornerGrid).
    public bool CornerOverlaysEnabled { get; set; } = true;
    public bool CornerOverlayShowLabel { get; set; } = true;
    public bool CornerOverlayShowSlotNumber { get; set; } = false;
    public double CornerOverlayLabelFontSize { get; set; } = 13.0;
    public string CornerOverlayLabelStyle { get; set; } = "Pill";
    public string CornerOverlayLabelFontFamily { get; set; } = ""; // "" = Segoe UI (WPF default)
    public string CornerOverlayLabelColor { get; set; } = "#E5E7EB"; // global default label text color
    public int CornerOverlayLabelHeight { get; set; } = 28; // WPF DIPs

    // Click a corner preview tile to bring that client to the centre (focus switch). Pure window
    // management — the click is NOT forwarded into the EVE client, so it stays EULA-compliant (no
    // input injection). A convenient alternative to the centre-seat hotkeys for users who haven't
    // set hotkeys up. See COMPLIANCE.md.
    public bool FocusPreviewOnClick { get; set; } = true;

    // Hover over a corner preview tile to peek at that client full-size over the master. The
    // hovered client temporarily moves to the master rect and is raised to the top of the Z-order
    // so it overlays the current master window; the master's window position is unchanged. On
    // mouse-leave the client is re-parked off-screen. Pure window management — no input forwarded.
    public bool HoverPreviewEnabled { get; set; } = true;

    // How long (ms) the mouse must rest on a corner tile before the peek triggers. A short delay
    // prevents accidental peeks when the cursor merely passes over a tile. 0 = instant.
    public int HoverPreviewDelayMs { get; set; } = 250;

    // Only fire hotkeys when an EVE client is in the foreground.
    public bool RequireEveFocusForHotkeys { get; set; } = true;

    // Throttle background EVE client processes to BELOW_NORMAL CPU priority while another is focused.
    // Reduces GPU/CPU competition so the active client gets more frame budget.
    public bool ThrottleBackgroundProcesses { get; set; } = true;

    // First-run setup wizard has been completed (controls whether it auto-shows on launch).
    public bool SetupCompleted { get; set; } = false;

    // Last main-window position (screen coordinates). Null until the window has been shown once;
    // restored on launch when still within the visible virtual screen.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    // User-supplied override for the EVE Launcher executable path, used when it isn't found at
    // either of the common install locations ClientLaunchService checks first.
    public string? EveLauncherPathOverride { get; set; }

    // Chat/event keyword alert rules watched by ChatLogWatcherService.
    public ObservableCollection<ChatAlertRule> ChatAlertRules { get; set; } = new();
}
