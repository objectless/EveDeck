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
    public double CornerOverlayLabelFontSize { get; set; } = 21.0;
    public string CornerOverlayLabelStyle { get; set; } = "Pill";
    public string CornerOverlayLabelFontFamily { get; set; } = "Acens"; // bundled font, see Assets\Fonts\Acens-LICENSE.txt
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

    // What hovering a corner tile does: "Peek" temporarily raises the real client over the master
    // (the original behaviour); "Zoom" magnifies just the preview thumbnail in place — the real
    // window is never moved. Zoom is the eve-o-preview-style option.
    public string HoverPreviewStyle { get; set; } = "Peek";

    // Preview magnification factor for the Zoom hover style (1.5–4x).
    public double HoverZoomFactor { get; set; } = 2.0;

    // Only fire hotkeys when an EVE client is in the foreground.
    public bool RequireEveFocusForHotkeys { get; set; } = true;

    // Throttle background EVE client processes to BELOW_NORMAL CPU priority while another is focused.
    // Reduces GPU/CPU competition so the active client gets more frame budget.
    public bool ThrottleBackgroundProcesses { get; set; } = true;

    // Auto-minimize EVE clients that are not foreground (skipping NeverMinimize seats). Flat
    // layouts only — corner-overlay mode needs unminimized windows for live thumbnails, so the
    // option is ignored while corner overlays are live.
    public bool AutoMinimizeInactiveClients { get; set; } = false;

    // Hide a corner tile's live preview while that seat's client IS the foreground window (it's
    // already on screen full-size, the tile is redundant clutter). eve-o-preview's
    // "hide active client thumbnail" equivalent.
    public bool HideActiveSeatTile { get; set; } = false;

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

    // Structured game-event alert rules watched by GameLogWatcherService (Gamelogs, not Chatlogs).
    // Seeded with defaults for fresh installs / pre-feature saves; JSON round-trip replaces the
    // collection, so user edits (including deleting every rule) persist as-is.
    public ObservableCollection<GameEventRule> GameEventRules { get; set; } = new(GameEventRule.Defaults());

    // Append the character's current solar system (tracked from Local chatlog headers) to the
    // corner-overlay labels.
    public bool CornerOverlayShowSystem { get; set; } = true;

    // Once every seat has been simultaneously offline (no live window for any seat) for this many
    // seconds, the corner overlay tears itself down instead of leaving a wall of stale "Name ·
    // offline" pills on screen after the whole session has ended. 0 = never auto-teardown.
    public int OfflineOverlayTimeoutSeconds { get; set; } = 60;

    // EveDeck-rendered Mumble talker overlay (fed by the EveDeck Mumble plugin over a named
    // pipe). Only Enabled/Locked/X/Y/OpacityPercent are used -- the window owns its own size.
    public UtilityOverlaySlot TalkerOverlay { get; set; } = new();

    // Profile Sync: manual character-id -> account (core_user) id overrides. Auto-pairing uses
    // file-mtime correlation, which the user can correct in the UI; corrections are kept here
    // so they survive restarts and stale mtimes.
    public Dictionary<string, string> ProfileCharAccountOverrides { get; set; } = new();

    // Apps allowed to visually sit above the corner-overlay tile/pill surfaces even while an EVE
    // client has focus (e.g. voice/intel tools the user keeps positioned over the game). Matched
    // by case-insensitive substring against the window's owning process name.
    public ObservableCollection<OverlayAllowedApp> OverlayAllowedApps { get; set; } = new()
    {
        new() { ProcessName = "mumble" },
        new() { ProcessName = "rift" },
        new() { ProcessName = "pyfa" },
        new() { ProcessName = "discord" },
        new() { ProcessName = "pidgin" },
    };
}
