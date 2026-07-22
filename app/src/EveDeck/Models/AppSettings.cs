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

    // Config profiles bundle a layout profile + a character set + the overlay appearance settings
    // into one named switch ("Mining" / "PvP"). See ConfigProfile for why the first two are stored
    // as references rather than copies. Switched from the tray menu and, optionally, on startup.
    public ObservableCollection<ConfigProfile> ConfigProfiles { get; set; } = new();
    public string ActiveConfigProfileId { get; set; } = "";
    public bool ApplyConfigProfileOnStartup { get; set; } = false;
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

    // Where the label sits WITHIN its tile, as a 3x3 anchor. One of:
    //   TopLeft    TopCenter    TopRight
    //   MiddleLeft Center       MiddleRight
    //   BottomLeft BottomCenter BottomRight
    // Labels are drawn inside the tile bounds rather than as a full-width strip above/below it,
    // matching how EVE-O Preview overlays its label on the thumbnail itself. Center is the default.
    // Unrecognised values fall back to Center -- see LabelSurfaceWindow.ParseAnchor.
    public string CornerOverlayLabelAnchor { get; set; } = "Center";

    // Inset in WPF DIPs between the label and the tile edge it is anchored to. Ignored on whichever
    // axis the anchor is centered. EVE-O Preview uses a fixed 8px inset; this is the same idea, made
    // adjustable because EveDeck's labels can be far larger than its 8.25pt one.
    public int CornerOverlayLabelInset { get; set; } = 8;

    // MASTER-pill override for the anchor above. Empty = inherit CornerOverlayLabelAnchor. Defaults
    // to TopCenter because the master rect is the client you are actually looking at: a label parked
    // in the middle of it sits over the ship/HUD, whereas the small corner tiles read better with the
    // name centered on the thumbnail. Per-seat SlotAssignment.LabelAnchorMaster beats this.
    public string CornerOverlayLabelAnchorMaster { get; set; } = "TopCenter";

    // Hide every preview tile while no EVE client (and not EveDeck itself) is the foreground app,
    // so alt-tabbing to a browser, Discord or a spreadsheet leaves the previews out of the way
    // instead of floating over them. Mirrors EVE-O Preview's HideThumbnailsOnLostFocus. The delay
    // stops a quick alt-tab, or the brief foreground gap during a seat swap, from flickering the
    // whole overlay off and straight back on.
    public bool HidePreviewsOnFocusLoss { get; set; } = false;
    public double HidePreviewsOnFocusLossDelaySeconds { get; set; } = 1.0;

    // Snap on-overlay tile drags/resizes to a pixel grid. 0 disables snapping. Mirrors EVE-O
    // Preview's EnableThumbnailSnap, but as a size rather than a bool so the grid can be tuned.
    public int CornerOverlaySnapGridPx { get; set; } = 0;

    // Which point a hover-zoomed tile grows FROM, as one of the same nine 3x3 names used by
    // CornerOverlayLabelAnchor. "Center" grows evenly in all directions (the original behavior);
    // an edge/corner anchor pins that edge so a tile near a screen edge expands inward instead of
    // being clamped. Per-seat SlotAssignment.ZoomAnchor overrides this.
    public string HoverZoomAnchor { get; set; } = "Center";

    // Ask Windows to power-throttle (EcoQoS) EVE clients that are not the foreground window, on top
    // of the existing ThrottleBackgroundProcesses priority drop. EcoQoS parks those processes on
    // efficiency cores and lets the scheduler clock them down, which is the EULA-compliant way to
    // stop background clients burning CPU/GPU.
    //
    // Deliberately NOT a frame-rate limiter: capping another process's FPS means hooking its D3D
    // present chain via DLL injection, which AGENTS.md forbids outright. EcoQoS is pure OS-level
    // scheduling -- nothing is injected, read, or written in the EVE client. See COMPLIANCE.md.
    public bool EcoQosBackgroundClients { get; set; } = false;

    // Keep the next seat in the cycle order OUT of EcoQoS, so the client you are about to switch to
    // is already running at full speed when you get there. EVE-O Plus's "predictive" limiting idea,
    // done with scheduling instead of frame caps.
    public bool EcoQosExemptNextInCycle { get; set; } = true;

    // Hide a preview tile while its client is still sitting on the EVE login/character-select screen,
    // where the thumbnail shows nothing useful. Mirrors EVE-O Preview's HidePreviewAtLoginScreen.
    // Detection is by window title only (EVE titles the window "EVE" with no character name until a
    // character is selected) -- no memory reading, no injection.
    public bool HidePreviewsAtLoginScreen { get; set; } = false;

    // Global default label font/size/color for the MASTER (centered, near-full-size) seat's pill.
    // Empty/null = inherit the normal CornerOverlayLabelFontFamily/FontSize/LabelColor above, so
    // this is a no-op until explicitly customized. Per-seat overrides on SlotAssignment
    // (LabelFontFamilyMaster etc.) take precedence over these when set.
    public string CornerOverlayLabelFontFamilyMaster { get; set; } = "";
    public double? CornerOverlayLabelFontSizeMaster { get; set; } = null;
    public string CornerOverlayLabelColorMaster { get; set; } = "";

    // Text style toggles for corner-overlay preview labels. Bold/Italic are plain font-weight/style
    // switches. DropShadow defaults true because the "IconText" label style has always rendered a
    // soft black shadow behind its plain-text name for legibility over bright video (this used to be
    // hardcoded); true preserves that exact look with no action needed, and additionally lets "Pill"
    // style labels opt in too (harmless there since Pill text already sits on an opaque dark chip).
    // Outline draws a black stroke around the glyphs; off by default (a new, purely additive look).
    public bool CornerOverlayLabelBold { get; set; } = false;
    public bool CornerOverlayLabelItalic { get; set; } = false;
    public bool CornerOverlayLabelDropShadow { get; set; } = true;
    public bool CornerOverlayLabelOutline { get; set; } = false;

    // Global MASTER-pill overrides for the style toggles above. null = inherit the normal toggle
    // (same fallback pattern as CornerOverlayLabelFontFamilyMaster etc.); per-seat overrides on
    // SlotAssignment (LabelBoldMaster etc.) take precedence over these when set.
    public bool? CornerOverlayLabelBoldMaster { get; set; } = null;
    public bool? CornerOverlayLabelItalicMaster { get; set; } = null;
    public bool? CornerOverlayLabelDropShadowMaster { get; set; } = null;
    public bool? CornerOverlayLabelOutlineMaster { get; set; } = null;

    // Global default label opacity (0-100%, WPF-Opacity-style whole-label fade) and its MASTER-pill
    // override. null master = inherit the normal opacity (same fallback pattern as the style toggles).
    public int CornerOverlayLabelOpacity { get; set; } = 100;
    public int? CornerOverlayLabelOpacityMaster { get; set; } = null;

    // Preview-tile opacity (0-100%, DWM thumbnail alpha) for every corner/master DWM preview. One
    // global slider applied uniformly -- no per-seat/master split like the label opacity above.
    public int CornerOverlayPreviewOpacity { get; set; } = 100;

    // Click a corner preview tile to bring that client to the center (focus switch). Pure window
    // management — the click is NOT forwarded into the EVE client, so it stays EULA-compliant (no
    // input injection). A convenient alternative to the center-seat hotkeys for users who haven't
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

    // Structured game-event alert rules watched by GameLogWatcherService (Gamelogs, not Chatlogs).
    // Seeded with defaults for fresh installs / pre-feature saves; JSON round-trip replaces the
    // collection, so user edits (including deleting every rule) persist as-is.
    public ObservableCollection<GameEventRule> GameEventRules { get; set; } = new(GameEventRule.Defaults());

    // ── Toast notification placement + native OS mirror ───────────────────────
    // Corner/edge the toast stack anchors to. One of: TopLeft, TopCenter, TopRight, BottomLeft,
    // BottomCenter, BottomRight. Bottom* stacks grow upward (newest nearest the anchored edge),
    // Top* stacks grow downward -- see ToastAnchor in ToastNotificationWindow.
    public string ToastPosition { get; set; } = "BottomRight";
    // Also mirrors every toast into the real Windows Notification Center (SuppressPopup -- no
    // second banner, EveDeck's own styled popup stays the only visible one) so alerts are still
    // reviewable from the system clock's flyout after EveDeck's popup has faded. Best-effort: OS
    // notification plumbing this app doesn't control (AUMID registration, the user's own Windows
    // notification settings) can silently no-op this without affecting the primary toast pipeline.
    public bool NativeNotificationCenterEnabled { get; set; } = true;

    // Abyss Mode: suppresses the SOUND on tile-glow (FlashOnTile) game events while enabled, but
    // keeps the visual glow. Abyssal Deadspace lets up to three characters take continuous combat
    // damage simultaneously in their own instances -- without this the Combat rule's default sound
    // would fire almost constantly for the whole run. A manual session toggle rather than a
    // permanent per-rule setting, since the user still wants the sound during normal play.
    public bool AbyssModeEnabled { get; set; }

    // Toast notifications assert the topmost slot ahead of the corner-overlay surfaces AND any
    // Overlay Allow List app, so an alert is never buried behind a preview tile or a docked
    // Discord/Mumble window. Off means toasts take their chances in the topmost band like any other
    // window -- allow-listed apps re-assert themselves every tick while EVE is focused, so they will
    // generally end up on top.
    public bool ToastsAboveOverlays { get; set; } = true;

    // Append the character's current solar system (tracked from Local chatlog headers) to the
    // corner-overlay labels.
    public bool CornerOverlayShowSystem { get; set; } = true;

    // Once every seat has been simultaneously offline (no live window for any seat) for this many
    // seconds, the corner overlay tears itself down instead of leaving a wall of stale "Name ·
    // offline" pills on screen after the whole session has ended. 0 = never auto-teardown.
    public int OfflineOverlayTimeoutSeconds { get; set; } = 60;

    // Hides a seat's "Name · offline" pill after it has been continuously offline for this many
    // seconds. 0 = hide immediately (no offline text ever shown). -1 (default) = never hide,
    // preserving the original always-on behavior. Independent of OfflineOverlayTimeoutSeconds,
    // which only tears down the WHOLE overlay once EVERY seat is offline at once.
    public int OfflinePillTimeoutSeconds { get; set; } = -1;

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

    // Non-EVE apps whose windows also show up as detected/assignable windows so they can be
    // previewed in a corner tile alongside EVE clients. Empty by default -- opt-in per app, unlike
    // OverlayAllowedApps above (which ships with sensible defaults for a DIFFERENT purpose).
    public ObservableCollection<PreviewableApp> PreviewableApps { get; set; } = new();

    // ── Planetary Industry (Planets tab) ──────────────────────────────────────
    // Read-only ESI colony monitor + factory-load calculator. Off until the user opts in, since it
    // needs the esi-planets scope which older linked characters won't have granted.
    public bool PiEnabled { get; set; } = false;
    // Poll cadence. ESI caches colonies for ~10 min server-side, so polling faster just burns the
    // error budget for stale data.
    public int PiRefreshMinutes { get; set; } = 10;
    // Alert (seat flash + sound) when an extractor is within this many hours of expiring.
    public double PiExtractorAlertHours { get; set; } = 6.0;
    // Alert when a colony's fullest storage/launchpad passes this fill percentage.
    public int PiStorageAlertPercent { get; set; } = 90;

    // The character whose station assets the factory-load calculator totals against — typically
    // whoever hauls hauled-in P1/P2/etc from the extractor alts and holds the working stockpile.
    // Null = fall back to summing every linked character's assets (can double-count material still
    // sitting on extractor alts that hasn't been consolidated yet).
    public long? PiConsolidationCharacterId { get; set; } = null;

    // Factory-load calculator inputs. FactoryInputTypeIds are the P1 (or any tier) commodity type ids
    // whose stock gets split across PiFactoryCount factories, each burning PiFactoryBurnPerHour of
    // every input per hour.
    public ObservableCollection<int> PiFactoryInputTypeIds { get; set; } = new();
    public int PiFactoryCount { get; set; } = 16;
    public double PiFactoryBurnPerHour { get; set; } = 240;

    // Type ids the user has unchecked from gating the split's "scarcest input" calc — see
    // PiFactoryInput.IncludeInSplit. Typically an intermediate tier consumed entirely on-planet
    // (chained straight into the next facility) rather than genuinely hauled/staged stock.
    public ObservableCollection<int> PiFactoryExcludedInputTypeIds { get; set; } = new();
}
