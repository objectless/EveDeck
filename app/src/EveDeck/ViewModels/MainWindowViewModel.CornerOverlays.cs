using System.Collections.ObjectModel;
using EveDeck.Models;
using EveDeck.Views;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    // -- Model A occupancy --------------------------------------------------------
    // Seats (SlotAssignment.SlotNumber) are FIXED accounts -- their Label (main character),
    // AssignedWindows, etc. never move. What changes is which seat occupies the center rect and
    // which seat shows at each corner POSITION. A "position id" is the non-master profile slot
    // number whose rect defines that corner; positions are fixed for the session, occupants rotate.
    //
    // With multiple swap groups each group is an independent swap ring with its own center slot and
    // master seat. All per-group occupancy is keyed by groupId (SwapGroup.GroupId or "__single__").
    // EffectiveGroups() synthesises a single all-slots group when no groups are defined so legacy
    // behaviour is preserved automatically.

    // ONE window hosts every tile thumbnail and ONE (owned by it, so the OS keeps it above) hosts
    // every label. Tiles + pills are keyed by POSITION id; center pills are stored under the group's
    // center slot number. With a single HWND per surface there is no per-tile z-order to maintain.
    private TileSurfaceWindow? _tileSurface;
    private LabelSurfaceWindow? _labelSurface;
    private readonly Dictionary<int, nint> _cornerSourceHandles = new();
    private readonly Dictionary<int, bool> _cornerPreventedState = new();
    private readonly Dictionary<int, WindowRect> _cornerRects = new();
    private int _cursorOverPosition = -1;

    // Safety-net timestamp for the low-frequency z-order re-assert in MaintainCornerOverlays -- a
    // catch-all for any missed/unknown trigger path, distinct from the historical per-tick flicker
    // bug (many windows fighting via HIGH FREQUENCY reassertion): this is one infrequent, idempotent
    // re-assert of our own already-correctly-ordered surfaces, not a contest between windows.
    private DateTime _lastZOrderSafetyNet = DateTime.MinValue;

    // HWND of the on-monitor layout editor while it's open (0 = none). The overlay surfaces are
    // always-topmost now (see ApplySurfaceZOrder), so without this the editor's own resize/drag
    // chrome can end up rendered BEHIND the preview tiles the moment it becomes foreground and the
    // WinEvent hook re-asserts our topmost surfaces on top of it. Set/cleared by EditLayoutOnMonitor.
    private nint _layoutEditorHwnd;

    // Toast notification surface (chat keyword / non-combat game event alerts). Created lazily on
    // first use, then kept alive for the app's session (unlike the corner-overlay surfaces, which
    // get torn down/rebuilt on layout changes) since toasts fire independently of whether corner
    // overlays are even running.
    private Views.ToastNotificationWindow? _toastWindow;
    private nint _toastHwnd;

    // True while the corner-overlay surfaces exist (the overlays are live on screen).
    internal bool CornerOverlaysLive => _tileSurface is not null;

    // Corner position waiting for the debounce delay to fire. -1 = none pending.
    private int _pendingHoverPosition = -1;

    // Peek-swap state — a temporary move-swap that reverts when the cursor leaves.
    // Corner seats always live parked off-screen (ResolveParkRect), never at their tile's rect —
    // the tile only ever shows a DWM/WGC thumbnail. So a peek is just master <-> park, independent
    // of the tile's own geometry.
    private int _peekPosition = -1;       // corner position currently peeked; -1 = none
    private int _peekSeat = -1;           // seat moved to master rect
    private int _peekCenteredSeat = -1;   // seat displaced from master rect to park
    private string? _peekGroupId;
    private WindowRect? _peekMasterRect;  // master rect at peek time (extends hover zone)
    private WindowRect? _peekParkRect;    // park rect the displaced master seat was sent to

    // Set the moment every seat first goes simultaneously offline; cleared the instant any seat's
    // client is detected again. MaintainCornerOverlays tears the overlay down once this has held
    // for OfflineOverlayTimeoutSeconds, instead of leaving a wall of stale "Name · offline" pills
    // on screen after the whole session has ended.
    private DateTime? _allSeatsOfflineSince;

    // HidePreviewsOnFocusLoss state: when the foreground last left EVE/EveDeck, and whether the
    // surfaces are currently parked out of sight because of it. See UpdateFocusLossHiding.
    private DateTime? _focusLostSince;
    private bool _previewsHiddenByFocusLoss;

    // First-observed-offline timestamp per seat (SlotAssignment.SlotNumber), used by
    // OfflinePillTimeoutSeconds to hide an individual seat's offline pill after it's been
    // offline "too long". Updated once per tick in MaintainCornerOverlays, read (never mutated)
    // by OfflinePillText — keeps a single source of truth instead of scattering timer logic
    // across every call site that renders a pill.
    private readonly Dictionary<int, DateTime> _seatOfflineSince = new();

    // Per-group occupancy. In legacy (single-group) mode all dicts have a single key "__single__".
    private readonly Dictionary<string, int> _centeredSeatByGroup = new();
    private readonly Dictionary<string, int> _baseMasterByGroup = new();
    private readonly Dictionary<string, Dictionary<int, int>> _cornerSeatByGroup = new();
    private readonly Dictionary<string, Dictionary<int, int>> _homeOccupantByGroup = new();
    private readonly Dictionary<string, Dictionary<int, int>> _homePositionByGroup = new();

    // -- Swap group helpers -------------------------------------------------------

    private IReadOnlyList<SwapGroup> EffectiveGroups()
    {
        if (SelectedProfile is null) return Array.Empty<SwapGroup>();
        if (SelectedProfile.SwapGroups.Count > 0) return SelectedProfile.SwapGroups;
        var allSlots = SelectedProfile.Slots.Select(s => s.SlotNumber).ToList();
        return new[] { new SwapGroup { GroupId = "__single__", Name = "Default", SlotNumbers = allSlots } };
    }

    private int CenterSlotForGroup(SwapGroup group)
    {
        if (SelectedProfile is null) return CenterSlotNumber;
        IEnumerable<LayoutSlot> groupSlots = group.SlotNumbers.Count == 0
            ? SelectedProfile.Slots
            : SelectedProfile.Slots.Where(s => group.SlotNumbers.Contains(s.SlotNumber));
        return groupSlots.MaxBy(s => (long)s.Width * s.Height)?.SlotNumber ?? CenterSlotNumber;
    }

    private int OccupantAtPosition(int position)
    {
        foreach (var (_, corners) in _cornerSeatByGroup)
            if (corners.TryGetValue(position, out var s)) return s;
        foreach (var group in EffectiveGroups())
            if (CenterSlotForGroup(group) == position)
                return _centeredSeatByGroup.GetValueOrDefault(group.GroupId, position);
        return position;
    }

    private SwapGroup? FindGroupForSeat(int seat)
    {
        foreach (var group in EffectiveGroups())
        {
            var gid = group.GroupId;
            if (_centeredSeatByGroup.TryGetValue(gid, out var c) && c == seat) return group;
            if (_cornerSeatByGroup.TryGetValue(gid, out var cs) && cs.ContainsValue(seat)) return group;
        }
        foreach (var group in EffectiveGroups())
        {
            var gid = group.GroupId;
            if (_homePositionByGroup.TryGetValue(gid, out var hp) && hp.ContainsKey(seat)) return group;
        }
        return EffectiveGroups().FirstOrDefault();
    }

    // -- Home arrangement --------------------------------------------------------

    internal Dictionary<int, int> ComputeHomeArrangement()
    {
        var result = new Dictionary<int, int>();
        if (SelectedProfile is null) return result;

        var master = ActiveMasterSeat;
        var center = CenterSlotNumber;
        var cornerPositions = SelectedProfile.Slots
            .Select(s => s.SlotNumber).Where(n => n != center).OrderBy(n => n).ToList();
        var remaining = SelectedProfile.Slots
            .Select(s => s.SlotNumber).Where(n => n != master).OrderBy(n => n).ToList();

        var openPositions = new List<int>();
        foreach (var p in cornerPositions)
        {
            var hs = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == p)?.HomeSeat;
            if (hs.HasValue && hs.Value != master && remaining.Remove(hs.Value))
                result[p] = hs.Value;
            else
                openPositions.Add(p);
        }

        foreach (var p in openPositions.ToList())
        {
            if (remaining.Remove(p)) { result[p] = p; openPositions.Remove(p); }
        }

        foreach (var (p, seat) in openPositions.Zip(remaining))
            result[p] = seat;

        return result;
    }

    internal void ResetCornerOccupancy()
    {
        _centeredSeatByGroup.Clear();
        _baseMasterByGroup.Clear();
        _cornerSeatByGroup.Clear();
        _homeOccupantByGroup.Clear();
        _homePositionByGroup.Clear();
        if (SelectedProfile is null) return;

        var fullArrangement = ComputeHomeArrangement();
        foreach (var group in EffectiveGroups())
            ResetGroupOccupancy(group, fullArrangement);
    }

    private void ResetGroupOccupancy(SwapGroup group, Dictionary<int, int> fullArrangement)
    {
        var groupId = group.GroupId;
        var groupCenter = CenterSlotForGroup(group);

        int masterSeat;
        if (groupCenter == CenterSlotNumber)
            masterSeat = ActiveMasterSeat;
        else
            masterSeat = fullArrangement.GetValueOrDefault(groupCenter, groupCenter);

        Dictionary<int, int> groupCorners;
        if (group.SlotNumbers.Count == 0)
        {
            groupCorners = new Dictionary<int, int>(fullArrangement);
        }
        else
        {
            groupCorners = fullArrangement
                .Where(kv => group.SlotNumbers.Contains(kv.Key) && kv.Key != groupCenter)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        _centeredSeatByGroup[groupId] = masterSeat;
        _baseMasterByGroup[groupId] = masterSeat;
        _cornerSeatByGroup[groupId] = groupCorners;
        _homeOccupantByGroup[groupId] = new Dictionary<int, int>(groupCorners);
        _homePositionByGroup[groupId] = groupCorners.ToDictionary(kv => kv.Value, kv => kv.Key);
    }

    // -- Mini-map editor ---------------------------------------------------------

    internal void SetSeatHomeCorner(int seat, int position)
    {
        if (SelectedProfile is null) return;
        if (Seat(seat) is null) return;

        if (position == CenterSlotNumber)
        {
            SetMasterSlot(Seat(seat));
            return;
        }

        if (seat == ActiveMasterSeat)
        {
            Log.Warn("The master always sits in the center. Drop it on the center cell, or set a different master first.");
            return;
        }

        var targetSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == position);
        if (targetSlot is null) return;

        var arrangement = ComputeHomeArrangement();
        if (arrangement.GetValueOrDefault(position) == seat) return;
        var displaced = arrangement.GetValueOrDefault(position);
        var oldPosition = arrangement.FirstOrDefault(kv => kv.Value == seat).Key;

        targetSlot.HomeSeat = seat;
        if (oldPosition != 0 && oldPosition != position && displaced != 0)
        {
            var oldSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == oldPosition);
            if (oldSlot is not null) oldSlot.HomeSeat = displaced;
        }

        Save();
        ReapplyCornerHomes();
        Log.Info($"Set {SeatLabel(seat)} home corner to {CornerCode(position)}.");
    }

    private void ReapplyCornerHomes()
    {
        var overlaysLive = CornerOverlaysLive;
        ResetCornerOccupancy();
        UpdatePositionCodes();
        RebuildMiniMap();
        if (overlaysLive)
        {
            StopCornerOverlays();
            StartCornerOverlays();
        }
    }

    private Dictionary<int, int>? BuildGroupTargetOccupancy(string groupId, int seat)
    {
        if (!_homeOccupantByGroup.TryGetValue(groupId, out var homeOccupant) || homeOccupant.Count == 0) return null;
        if (!_homePositionByGroup.TryGetValue(groupId, out var homePosition)) return null;

        var masterSeat = _baseMasterByGroup.GetValueOrDefault(groupId, ActiveMasterSeat);

        var target = new Dictionary<int, int>(homeOccupant);
        if (seat == masterSeat) return target;

        if (!homePosition.TryGetValue(seat, out var seatHome)) return null;
        target[seatHome] = masterSeat;
        return target;
    }

    // -- Seat lookups ------------------------------------------------------------

    private SlotAssignment? Seat(int seat) => Assignments.FirstOrDefault(a => a.SlotNumber == seat);
    private EveWindowInfo? FindSeatWindow(int seat)
    {
        var a = Seat(seat);
        return a is null ? null : FindAssignedWindows(a).FirstOrDefault();
    }
    private string SeatLabel(int seat) => Seat(seat)?.DisplayLabel ?? "";

    // True when an EVE client's window title indicates it hasn't selected a character yet -- EVE
    // titles that window plainly "EVE" (no " - Character Name" suffix) until past the login/
    // character-select screen. Backs AppSettings.HidePreviewsAtLoginScreen. Title-only, same
    // CharacterNameFromTitle convention used everywhere else in this file (MainWindowViewModel.Clients.cs)
    // -- a title with no "EVE - " prefix to strip comes back unchanged, i.e. still literally "EVE".
    private static bool IsAtLoginScreen(string title) =>
        CharacterNameFromTitle(title).Equals("EVE", StringComparison.OrdinalIgnoreCase);

    // -- Create / show -----------------------------------------------------------

    internal void StartCornerOverlays()
    {
        StopCornerOverlays();

        if (!_settings.CornerOverlaysEnabled) return;
        if (SelectedProfile is null || !SelectedProfile.SupportsCornerGrid) return;

        EnsureValidMasterSeat();

        if (_centeredSeatByGroup.Count == 0) ResetCornerOccupancy();
        UpdatePositionCodes();

        var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var dpiScale = monitor is null ? 1.0 : monitor.DpiX / 96.0;

        var slotRects = SelectedProfile.Slots.ToDictionary(s => s.SlotNumber, s => ResolvePlacementRect(s));

        // Surface bounds: the layout monitor, or (for multi-monitor custom profiles, whose tiles may
        // land on a second monitor) the union of the slot rects so every tile has surface to render on.
        int surfX, surfY, surfW, surfH;
        if (monitor is not null && !IsMultiMonitorProfile(SelectedProfile))
        {
            // Never size the tile surface to EXACTLY the monitor. A monitor-exact topmost layered
            // window is treated by DWM as a fullscreen surface, so its registered DWM thumbnails stop
            // compositing -- the previews go blank while the pills (on the differently-sized label
            // window) still draw. Shrinking a few px (same AvoidExactMonitorMatch mitigation used for
            // the master window) is imperceptible and keeps every tile rect inside the surface.
            var surf = AvoidExactMonitorMatch(new WindowRect
            {
                X = monitor.Bounds.X, Y = monitor.Bounds.Y,
                Width = monitor.Bounds.Width, Height = monitor.Bounds.Height
            });
            surfX = surf.X; surfY = surf.Y; surfW = surf.Width; surfH = surf.Height;
        }
        else
        {
            surfX = slotRects.Values.Min(r => r.X);
            surfY = slotRects.Values.Min(r => r.Y);
            surfW = Math.Max(1, slotRects.Values.Max(r => r.X + r.Width) - surfX);
            surfH = Math.Max(1, slotRects.Values.Max(r => r.Y + r.Height) - surfY);
        }

        var groupCenterSlots = EffectiveGroups()
            .ToDictionary(g => g.GroupId, g => CenterSlotForGroup(g));

        _tileSurface = new TileSurfaceWindow(surfX, surfY, surfW, surfH);
        _tileSurface.SnapGridPx = Math.Max(0, _settings.CornerOverlaySnapGridPx);
        _tileSurface.TileClicked = OnCornerTileClicked;
        _tileSurface.TileShiftClicked = OnCornerTileShiftClicked;
        _tileSurface.TileRectChanged = OnCornerTileRectChanged;
        _tileSurface.TileDragStarted = OnCornerTileDragStarted;
        _tileSurface.TileDragging = OnCornerTileDragging;
        _tileSurface.SetOpacity(_settings.CornerOverlayPreviewOpacity);
        _tileSurface.Show();

        if (_settings.CornerOverlayShowLabel)
        {
            _labelSurface = new LabelSurfaceWindow(surfX, surfY, surfW, surfH, dpiScale, _settings);
            _labelSurface.SetOwner(_tileSurface.Handle);
            _labelSurface.Show();
        }

        var allGroupCenterSlotNums = groupCenterSlots.Values.ToHashSet();
        foreach (var slot in SelectedProfile.Slots.Where(s => !allGroupCenterSlotNums.Contains(s.SlotNumber)))
        {
            var position = slot.SlotNumber;
            var rect = slotRects[position];

            _tileSurface.AddTile(position, rect.X, rect.Y, rect.Width, rect.Height);
            _cornerRects[position] = rect;

            var seat = OccupantAtPosition(position);
            var window = FindSeatWindow(seat);
            CreatePill(position, seat, rect,
                window is not null ? PillTextForPosition(position) : OfflinePillText(seat), SeatPortrait(seat));

            _tileSurface.SetSource(position, window?.Handle ?? 0);
            _cornerSourceHandles[position] = window?.Handle ?? 0;
        }

        foreach (var group in EffectiveGroups())
        {
            var groupCenter = groupCenterSlots[group.GroupId];
            if (!slotRects.TryGetValue(groupCenter, out var masterRect)) continue;
            var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(group.GroupId, 0);
            var centerPillText = FindSeatWindow(centeredSeat) is not null ? CenterPillTextForGroup(group.GroupId) : OfflinePillText(centeredSeat);
            CreatePill(groupCenter, centeredSeat, masterRect, centerPillText, SeatPortrait(centeredSeat), isMaster: true);

            var groupCenterSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == groupCenter);
            if (groupCenterSlot is not null && !HasDominantMasterSlot(groupCenterSlot))
            {
                // No dominant master area for this group (e.g. Grid family) -- the real master window
                // now runs at full resolution (see ResolveMasterRect) rather than being shrunk to this
                // cell, so the center needs its own live preview tile just like every corner.
                var centerWindow = FindSeatWindow(centeredSeat);
                _tileSurface.AddTile(groupCenter, masterRect.X, masterRect.Y, masterRect.Width, masterRect.Height);
                _cornerRects[groupCenter] = masterRect;
                _tileSurface.SetSource(groupCenter, centerWindow?.Handle ?? 0);
                _cornerSourceHandles[groupCenter] = centerWindow?.Handle ?? 0;
            }
        }

        ApplySurfaceZOrder();
        _frameTimer.Start();
    }

    private CharacterPortrait? SeatPortrait(int seat) => Seat(seat)?.RunningPortrait;

    // Effective label font (family, WPF size, colour hex) for a seat: the seat's own overrides win,
    // else the global defaults. Public so the Options / per-seat font pickers can seed their dialog.
    // isMaster=true resolves the MASTER-pill style instead: seat master override -> global master
    // default -> (if that's also unset) the normal (non-master) resolution below, so a Master style
    // is a no-op everywhere until someone explicitly sets one.
    public (string family, double size, string color) EffectiveSeatLabelFont(SlotAssignment seat, bool isMaster = false)
    {
        if (isMaster)
        {
            var normalFamily = !string.IsNullOrWhiteSpace(seat.LabelFontFamily) ? seat.LabelFontFamily! : _settings.CornerOverlayLabelFontFamily;
            var normalSize = seat.LabelFontSize ?? _settings.CornerOverlayLabelFontSize;
            var normalColor = !string.IsNullOrWhiteSpace(seat.LabelColor) ? seat.LabelColor! : _settings.CornerOverlayLabelColor;

            var family = !string.IsNullOrWhiteSpace(seat.LabelFontFamilyMaster) ? seat.LabelFontFamilyMaster!
                : !string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelFontFamilyMaster) ? _settings.CornerOverlayLabelFontFamilyMaster
                : normalFamily;
            var size = seat.LabelFontSizeMaster ?? _settings.CornerOverlayLabelFontSizeMaster ?? normalSize;
            var color = !string.IsNullOrWhiteSpace(seat.LabelColorMaster) ? seat.LabelColorMaster!
                : !string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelColorMaster) ? _settings.CornerOverlayLabelColorMaster
                : normalColor;
            return (family ?? "", size, color ?? "");
        }

        var famAlt = !string.IsNullOrWhiteSpace(seat.LabelFontFamily) ? seat.LabelFontFamily! : _settings.CornerOverlayLabelFontFamily;
        var sizeAlt = seat.LabelFontSize ?? _settings.CornerOverlayLabelFontSize;
        var colorAlt = !string.IsNullOrWhiteSpace(seat.LabelColor) ? seat.LabelColor! : _settings.CornerOverlayLabelColor;
        return (famAlt ?? "", sizeAlt, colorAlt ?? "");
    }

    private (string family, double size, string color) ResolveLabelFont(int seat, bool isMaster = false)
    {
        var s = Seat(seat);
        if (s is not null) return EffectiveSeatLabelFont(s, isMaster);
        if (!isMaster) return (_settings.CornerOverlayLabelFontFamily ?? "", _settings.CornerOverlayLabelFontSize, _settings.CornerOverlayLabelColor ?? "");
        var family = !string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelFontFamilyMaster) ? _settings.CornerOverlayLabelFontFamilyMaster : _settings.CornerOverlayLabelFontFamily;
        var size = _settings.CornerOverlayLabelFontSizeMaster ?? _settings.CornerOverlayLabelFontSize;
        var color = !string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelColorMaster) ? _settings.CornerOverlayLabelColorMaster : _settings.CornerOverlayLabelColor;
        return (family ?? "", size, color ?? "");
    }

    // Where this seat's label sits within its tile: per-seat override -> global master override ->
    // global default. Same chain as ResolveLabelFont, but with the two roles kept separate -- a seat
    // in a small corner tile and the SAME seat centered as master resolve independently, which is the
    // whole point (name centered on a thumbnail, out of the way at the top of the master rect).
    private string ResolveLabelAnchor(int seat, bool isMaster)
    {
        var s = Seat(seat);
        if (isMaster)
        {
            if (!string.IsNullOrWhiteSpace(s?.LabelAnchorMaster)) return s!.LabelAnchorMaster!;
            if (!string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelAnchorMaster)) return _settings.CornerOverlayLabelAnchorMaster;
            return _settings.CornerOverlayLabelAnchor;
        }
        if (!string.IsNullOrWhiteSpace(s?.LabelAnchor)) return s!.LabelAnchor!;
        return _settings.CornerOverlayLabelAnchor;
    }

    // Which edge/corner the hover-zoom magnification pins in place for this seat: per-seat
    // SlotAssignment.ZoomAnchor override -> AppSettings.HoverZoomAnchor global default. Reuses
    // LabelSurfaceWindow.ParseAnchor for the nine 3x3 name -> LabelAnchor resolution (same parser the
    // label-placement code already uses) rather than writing a second one.
    private LabelAnchor ResolveZoomAnchor(int seat)
    {
        var s = Seat(seat);
        var name = !string.IsNullOrWhiteSpace(s?.ZoomAnchor) ? s!.ZoomAnchor! : _settings.HoverZoomAnchor;
        return LabelSurfaceWindow.ParseAnchor(name);
    }

    // Effective label style flags (bold, italic, drop shadow, outline) for a seat. Mirrors
    // EffectiveSeatLabelFont's seat-override -> global-master -> global-default fallback chain,
    // kept as a separate method/tuple so the existing font (family/size/color) API is untouched.
    public (bool bold, bool italic, bool dropShadow, bool outline, int opacity) EffectiveSeatLabelStyle(SlotAssignment seat, bool isMaster = false)
    {
        var normalBold = seat.LabelBold ?? _settings.CornerOverlayLabelBold;
        var normalItalic = seat.LabelItalic ?? _settings.CornerOverlayLabelItalic;
        var normalShadow = seat.LabelDropShadow ?? _settings.CornerOverlayLabelDropShadow;
        var normalOutline = seat.LabelOutline ?? _settings.CornerOverlayLabelOutline;
        var normalOpacity = seat.LabelOpacity ?? _settings.CornerOverlayLabelOpacity;

        if (!isMaster) return (normalBold, normalItalic, normalShadow, normalOutline, normalOpacity);

        var bold = seat.LabelBoldMaster ?? _settings.CornerOverlayLabelBoldMaster ?? normalBold;
        var italic = seat.LabelItalicMaster ?? _settings.CornerOverlayLabelItalicMaster ?? normalItalic;
        var shadow = seat.LabelDropShadowMaster ?? _settings.CornerOverlayLabelDropShadowMaster ?? normalShadow;
        var outline = seat.LabelOutlineMaster ?? _settings.CornerOverlayLabelOutlineMaster ?? normalOutline;
        var opacity = seat.LabelOpacityMaster ?? _settings.CornerOverlayLabelOpacityMaster ?? normalOpacity;
        return (bold, italic, shadow, outline, opacity);
    }

    private (bool bold, bool italic, bool dropShadow, bool outline, int opacity) ResolveLabelStyle(int seat, bool isMaster = false)
    {
        var s = Seat(seat);
        if (s is not null) return EffectiveSeatLabelStyle(s, isMaster);
        if (!isMaster) return (_settings.CornerOverlayLabelBold, _settings.CornerOverlayLabelItalic, _settings.CornerOverlayLabelDropShadow, _settings.CornerOverlayLabelOutline, _settings.CornerOverlayLabelOpacity);
        var bold = _settings.CornerOverlayLabelBoldMaster ?? _settings.CornerOverlayLabelBold;
        var italic = _settings.CornerOverlayLabelItalicMaster ?? _settings.CornerOverlayLabelItalic;
        var shadow = _settings.CornerOverlayLabelDropShadowMaster ?? _settings.CornerOverlayLabelDropShadow;
        var outline = _settings.CornerOverlayLabelOutlineMaster ?? _settings.CornerOverlayLabelOutline;
        var opacity = _settings.CornerOverlayLabelOpacityMaster ?? _settings.CornerOverlayLabelOpacity;
        return (bold, italic, shadow, outline, opacity);
    }

    // True when `position` is any swap group's center/master slot. Needed because for layouts with
    // no dominant master area (the Grid family), the center slot is ALSO registered as a plain
    // corner tile and can be refreshed by the generic per-tick corner loop — without this check
    // that refresh would use the alt font instead of the master font it was created with.
    private bool IsGroupCenterPosition(int position) =>
        EffectiveGroups().Any(g => CenterSlotForGroup(g) == position);

    // Placement within the tile comes from the surface's own configured anchor
    // (AppSettings.CornerOverlayLabelAnchor), not from per-position geometry the way it used to --
    // every label now uses the same user-chosen 3x3 position, defaulting to center-center.
    private void CreatePill(int key, int seat, WindowRect rect, string text, CharacterPortrait? portrait, bool isMaster = false)
    {
        if (_labelSurface is null) return;
        var (family, size, color) = ResolveLabelFont(seat, isMaster);
        var (bold, italic, dropShadow, outline, opacity) = ResolveLabelStyle(seat, isMaster);
        _labelSurface.SetPill(key, rect, ResolveLabelAnchor(seat, isMaster), family, size, color, bold, italic, dropShadow, outline, opacity);
        _labelSurface.SetPillContent(key, text, portrait);
    }

    // -- Pill captions -----------------------------------------------------------

    private string PillTextForPosition(int position)
    {
        var occupant = OccupantAtPosition(position);
        var name = SeatLabel(occupant);
        var code = CornerCode(position);
        var text = _settings.CornerOverlayShowSlotNumber && !string.IsNullOrEmpty(code) ? $"{code} · {name}" : name;
        text = AppendSystem(text, occupant);
        // Shift+click on a tile toggles this (see OnCornerTileShiftClicked) -- a plain ASCII suffix
        // rather than a glyph, since the bundled label font's coverage of exotic Unicode isn't
        // guaranteed the way the arrow/bracket glyphs used elsewhere in this codebase are.
        if (Seat(occupant)?.ExcludedFromCycle == true) text += " (skip)";
        return text;
    }

    // "Name · Jita" when the seat's current solar system is known (Local chatlog tracking) and the
    // option is on; the bare text otherwise.
    private string AppendSystem(string text, int seat)
    {
        var system = SeatSystemName(seat);
        return system.Length > 0 && text.Length > 0 ? $"{text} · {system}" : text;
    }

    // Readable-on-dark colours to tint the system-name pill segment. Deterministic per system name
    // (same system -> same colour every session) so you can track at a glance which characters have
    // split off from the fleet's system.
    private static readonly string[] SystemPalette =
    {
        "#F87171", "#FB923C", "#FBBF24", "#A3E635", "#34D399", "#22D3EE",
        "#60A5FA", "#818CF8", "#C084FC", "#F472B6", "#2DD4BF", "#E879F9",
    };
    internal static string SystemColorHex(string system)
    {
        if (string.IsNullOrEmpty(system)) return "";
        var h = 0;
        foreach (var c in system) h = (h * 31 + c) & 0x7fffffff;
        return SystemPalette[h % SystemPalette.Length];
    }

    // Label for a seat whose client window is gone — keeps the seat identifiable instead of a
    // blank tile ("offline badge"). Empty when the seat itself has no label to show.
    private string OfflinePillText(int seat)
    {
        var timeout = _settings.OfflinePillTimeoutSeconds;
        if (timeout == 0) return "";
        if (timeout > 0 && _seatOfflineSince.TryGetValue(seat, out var since)
            && (DateTime.UtcNow - since).TotalSeconds >= timeout)
            return "";

        var name = SeatLabel(seat);
        return name.Length > 0 ? $"{name} · offline" : "";
    }

    // Directional arrow for a live-overlay pill, scoped to the seat's own swap group and excluding
    // that group's center/master slot from the bounding box. A group's master can sit on a totally
    // different monitor/scale than its own ring (e.g. a full-monitor master + a same-monitor 2x2 alt
    // grid) -- including it would skew the ring's L/R/T/B math and collapse distinct corners onto the
    // same bucket. GroupGridCodes is shared with UpdatePositionCodes so their arrow math can never
    // diverge again.
    private string CornerCode(int position)
    {
        var profile = SelectedProfile;
        if (profile is null || profile.Slots.Count == 0) return CircledNumeral(position);

        var group = EffectiveGroups().FirstOrDefault(g => g.SlotNumbers.Count == 0 || g.SlotNumbers.Contains(position))
            ?? EffectiveGroups().FirstOrDefault();
        if (group is null) return CircledNumeral(position);

        var codes = GroupGridCodes(profile, group);
        return codes.TryGetValue(position, out var code) ? code : CircledNumeral(position);
    }

    // Circled numerals (U+2460..U+2473 = 1..20) as the "cooler than a bare number" fallback for a
    // pill / position code when the geometric arrow-and-bracket codes can't be distinct -- e.g. alts
    // stacked in a single column, where every ring slot lands in the same L/C/R + T/M/B bucket.
    internal static string CircledNumeral(int n)
        => n is >= 1 and <= 20 ? ((char)('①' + n - 1)).ToString() : n.ToString();

    // Directional-arrow code for every ring slot (i.e. every slot in the group except its own
    // center/master slot), computed from a bounding box scoped to just that ring, with any bucket
    // collision degraded to plain slot numbers rather than showing a misleading duplicate arrow.
    private Dictionary<int, string> GroupGridCodes(LayoutProfile profile, SwapGroup group)
    {
        var centerSlotNum = CenterSlotForGroup(group);
        var groupSlots = group.SlotNumbers.Count == 0 ? profile.Slots : profile.Slots.Where(s => group.SlotNumbers.Contains(s.SlotNumber));
        var ringSlots = groupSlots.Where(s => s.SlotNumber != centerSlotNum).ToList();
        if (ringSlots.Count == 0) return new Dictionary<int, string>();

        var minX = ringSlots.Min(s => s.X);
        var minY = ringSlots.Min(s => s.Y);
        var totalW = Math.Max(1, ringSlots.Max(s => s.X + s.Width) - minX);
        var totalH = Math.Max(1, ringSlots.Max(s => s.Y + s.Height) - minY);

        var codes = ringSlots.ToDictionary(s => s.SlotNumber, s => GridCode(s, minX, minY, totalW, totalH));
        var hasCollision = codes.Values.GroupBy(c => c, StringComparer.Ordinal).Any(g => g.Count() > 1);
        if (hasCollision)
            foreach (var s in ringSlots) codes[s.SlotNumber] = CircledNumeral(s.SlotNumber);
        return codes;
    }

    private string CenterPillTextForGroup(string groupId)
    {
        var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(groupId, 0);
        var name = SeatLabel(centeredSeat);
        var text = _settings.CornerOverlayShowSlotNumber ? $"★ · {name}" : name;
        return AppendSystem(text, centeredSeat);
    }

    // -- Stop / teardown ---------------------------------------------------------

    internal void StopCornerOverlays()
    {
        _hoverPeekTimer.Stop();
        _pendingHoverPosition = -1;
        RevertPeekSwap();
        _cursorOverPosition = -1;

        // The surfaces are about to be destroyed, so any "hidden by focus loss" state refers to
        // windows that no longer exist -- clear it or a freshly rebuilt overlay starts out believing
        // it is already hidden and never shows itself.
        _focusLostSince = null;
        _previewsHiddenByFocusLoss = false;

        try { _labelSurface?.Close(); } catch { } // window may already be closed
        _labelSurface = null;
        try { _tileSurface?.Close(); } catch { } // window may already be closed
        _tileSurface = null;
        _cornerSourceHandles.Clear();
        _cornerRects.Clear();
        _seatOfflineSince.Clear();

        if (!_settings.ActiveFrameEnabled) _frameTimer.Stop();
    }

    // -- Center a seat -----------------------------------------------------------

    internal void CenterSeat(int seat)
    {
        if (SelectedProfile is null) return;

        // Per-seat "focus only": bring the window forward in place, never swap it into master. One
        // gate here covers every activation path (tile click, hotkeys, evedeck:// center) since they
        // all funnel through CenterSeat.
        if (Seat(seat)?.FocusOnlyNoSwap == true)
        {
            FocusSlot(seat);
            return;
        }

        if (!SelectedProfile.SupportsCornerGrid)
        {
            FocusSlot(seat);
            return;
        }

        var group = FindGroupForSeat(seat);
        if (group is null) { Log.Warn($"Seat {seat} is not in any swap group."); return; }

        if (!_settings.CornerOverlaysEnabled)
        {
            CenterSeatFlatInGroup(group, seat);
            return;
        }

        CenterSeatInGroup(group, seat);
    }

    private void CenterSeatInGroup(SwapGroup group, int seat)
    {
        var groupId = group.GroupId;
        var groupCenter = CenterSlotForGroup(group);
        var masterSlot = SelectedProfile!.Slots.FirstOrDefault(s => s.SlotNumber == groupCenter);
        if (masterSlot is null) { Log.Warn("Corner mode requires a layout with a center slot."); return; }

        if (_centeredSeatByGroup.Count == 0) ResetCornerOccupancy();

        var currentCenteredSeat = _centeredSeatByGroup.GetValueOrDefault(groupId, 0);
        if (seat == currentCenteredSeat)
        {
            var already = FindSeatWindow(seat);
            if (already is not null) { try { _windowService.FocusWindow(already.Handle); } catch { /* best-effort focus */ } }
            Log.Info($"Seat {seat} ({SeatLabel(seat)}) is already centered.");
            return;
        }

        var target = BuildGroupTargetOccupancy(groupId, seat);
        if (target is null)
        {
            Log.Warn($"Seat {seat} is not in the current corner arrangement for group '{group.Name}'; re-applying layout.");
            ApplyActiveProfile();
            return;
        }

        var incoming = FindSeatWindow(seat);
        var outgoing = FindSeatWindow(currentCenteredSeat);
        var masterRect = ResolveMasterRect(masterSlot);
        var parkRect = ResolveParkRect(masterRect);

        if (incoming is not null && outgoing is not null)
        {
            try
            {
                _windowService.SwapWindowPositions(
                    incoming.Handle, masterRect.X, masterRect.Y,
                    outgoing.Handle, parkRect.X, parkRect.Y);
            }
            catch (Exception ex) { Log.Error($"Center move failed: {ex.Message}"); return; }
        }
        else
        {
            try { if (incoming is not null) _windowService.MoveResizeWindow(incoming.Handle, masterRect); }
            catch (Exception ex) { Log.Error($"Could not center seat {seat}: {ex.Message}"); }
            try { if (outgoing is not null) _windowService.MoveResizeWindow(outgoing.Handle, parkRect); }
            catch (Exception ex) { Log.Error($"Could not park seat {currentCenteredSeat}: {ex.Message}"); }
        }

        var outgoingSeat = currentCenteredSeat;
        _centeredSeatByGroup[groupId] = seat;
        if (_cornerRects.ContainsKey(groupCenter)) UpdateCornerOverlay(groupCenter, incoming?.Handle ?? 0);

        var cornerSeat = _cornerSeatByGroup.TryGetValue(groupId, out var cs) ? cs : new Dictionary<int, int>();
        foreach (var (position, newSeat) in target)
        {
            if (cornerSeat.GetValueOrDefault(position, int.MinValue) == newSeat) continue;
            cornerSeat[position] = newSeat;
            _cornerSeatByGroup[groupId] = cornerSeat;
            var win = FindSeatWindow(newSeat);
            if (win is not null) UpdateCornerOverlay(position, win.Handle);
            else _cornerSourceHandles[position] = 0;
            RefreshPositionPill(position);
        }
        RefreshGroupCenterPill(group);

        // Focus BEFORE the z-order reassert, not after: SetForegroundWindow (inside FocusWindow) on
        // the newly-centered real EVE window can itself disturb z-order -- sometimes even when the OS
        // silently restricts the foreground grant, it still nudges the window up a band, which would
        // otherwise be the LAST z-order-affecting call and could leave the master window sitting
        // above the always-topmost overlay surfaces. Reasserting last guarantees the overlay wins.
        if (incoming is not null) { try { _windowService.FocusWindow(incoming.Handle); } catch { /* best-effort focus */ } }
        RefreshCornerOverlayZOrder();

        ScheduleAutoSave();
        Log.Info($"Centered seat {seat} ({SeatLabel(seat)}) in group '{group.Name}'; seat {outgoingSeat} ({SeatLabel(outgoingSeat)}) returned to its home corner.");
    }

    private void CenterSeatFlatInGroup(SwapGroup group, int seat)
    {
        var groupId = group.GroupId;
        var groupCenter = CenterSlotForGroup(group);
        var centerSlot = SelectedProfile!.Slots.FirstOrDefault(s => s.SlotNumber == groupCenter);
        if (centerSlot is null) { Log.Warn("Grid swap requires a layout with a center slot."); return; }

        if (_centeredSeatByGroup.Count == 0) ResetCornerOccupancy();

        var currentCenteredSeat = _centeredSeatByGroup.GetValueOrDefault(groupId, 0);
        if (seat == currentCenteredSeat)
        {
            var already = FindSeatWindow(seat);
            if (already is not null) { try { _windowService.FocusWindow(already.Handle); } catch { /* best-effort focus */ } }
            return;
        }

        var target = BuildGroupTargetOccupancy(groupId, seat);
        if (target is null)
        {
            Log.Warn($"Seat {seat} is not in the current arrangement; re-applying layout.");
            ApplyActiveProfile();
            return;
        }

        var cornerSeat = _cornerSeatByGroup.TryGetValue(groupId, out var cs) ? cs : new Dictionary<int, int>();

        int CurrentOccupant(int position) =>
            position == groupCenter ? currentCenteredSeat : cornerSeat.GetValueOrDefault(position, position);

        foreach (var slot in SelectedProfile.Slots.Where(s => group.SlotNumbers.Count == 0 || group.SlotNumbers.Contains(s.SlotNumber)))
        {
            var desired = slot.SlotNumber == groupCenter ? seat : target.GetValueOrDefault(slot.SlotNumber, slot.SlotNumber);
            if (CurrentOccupant(slot.SlotNumber) == desired) continue;

            var window = FindSeatWindow(desired);
            if (window is null) continue;
            try { _windowService.MoveResizeWindow(window.Handle, ResolvePlacementRect(slot)); }
            catch (Exception ex) { Log.Error($"Could not move seat {desired} to position {slot.SlotNumber}: {ex.Message}"); }
        }

        var outgoingSeat = currentCenteredSeat;
        _centeredSeatByGroup[groupId] = seat;
        cornerSeat.Clear();
        foreach (var (position, newSeat) in target) cornerSeat[position] = newSeat;
        _cornerSeatByGroup[groupId] = cornerSeat;

        var incoming = FindSeatWindow(seat);
        if (incoming is not null) { try { _windowService.FocusWindow(incoming.Handle); } catch { /* best-effort focus */ } }
        // Same ordering reasoning as CenterSeatInGroup: reassert AFTER focus so the overlay surfaces
        // (this path still registers a center tile when !HasDominantMasterSlot, see StartCornerOverlays)
        // are guaranteed to end up above the just-focused real window, not the other way around.
        RefreshCornerOverlayZOrder();

        UpdatePositionCodes();
        ScheduleAutoSave();
        Log.Info($"Centered seat {seat} ({SeatLabel(seat)}) in group '{group.Name}'; seat {outgoingSeat} ({SeatLabel(outgoingSeat)}) returned to its home corner.");
    }

    private void OnCornerTileClicked(int position)
    {
        if (!_settings.FocusPreviewOnClick) return;
        // Clear peek tracking without reverting — CenterSeat re-positions everything via HWNDs.
        ClearPeekState();
        _tileSurface?.ClearZoom();
        var seat = OccupantAtPosition(position);
        CenterSeat(seat);
    }

    // Shift+click: toggle this tile's occupant out of/into Cycle/CycleGroup without unassigning it
    // or touching the real window (mirrors EVE-O Preview's shift+click cycle-group toggle).
    private void OnCornerTileShiftClicked(int position)
    {
        var seat = OccupantAtPosition(position);
        var assignment = Seat(seat);
        if (assignment is null) return;
        assignment.ExcludedFromCycle = !assignment.ExcludedFromCycle;
        RefreshPositionPill(position);
        Save();
        Log.Info($"Seat {seat} ({SeatLabel(seat)}) {(assignment.ExcludedFromCycle ? "excluded from" : "included in")} cycling.");
    }

    // Right-drag (move) / both-buttons-drag (resize) directly on the overlay, mirroring EVE-O
    // Preview/EVE-APM Preview. The dragged tile's rect has already visually settled by the time
    // this fires (TileSurfaceWindow only raises it on mouse-up) -- this just needs to PERSIST it.
    //
    // Rather than poke the one changed LayoutSlot's raw X/Y/Width/Height directly, this reuses
    // ApplyEditedSlots (the same persistence path the on-monitor Layout Editor uses): snapshot every
    // OTHER position's currently-resolved rect, substitute the dragged one, and replace the whole
    // slot list atomically. Necessary because ResolvePlacementRect's scaling paths (capture-relative
    // and bounding-box-relative) derive from the WHOLE profile's slot geometry -- overwriting a
    // single slot's raw numbers without going through the same path other slots use could silently
    // shift every other slot's resolved position next time the profile is applied.
    private void OnCornerTileRectChanged(int position, int physX, int physY, int physWidth, int physHeight)
    {
        if (SelectedProfile is null || SelectedProfile.Slots.Count == 0) return;

        // ApplyEditedSlots doesn't always restart the whole overlay (only when cloning off a
        // built-in profile) -- keep our own hover-hit-test rect in sync immediately regardless, since
        // TileSurfaceWindow's own _tiles rect (what's actually drawn) already updated live during the
        // drag and must not drift from what MaintainCornerOverlays hit-tests hover against.
        _cornerRects[position] = new WindowRect { X = physX, Y = physY, Width = physWidth, Height = physHeight };

        var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        if (monitor is null) return;

        var items = SelectedProfile.Slots
            .OrderBy(s => s.SlotNumber)
            .Select(s =>
            {
                if (s.SlotNumber == position)
                    return new Views.LayoutEditorSlot { SlotNumber = s.SlotNumber, Label = s.Label, X = physX, Y = physY, Width = physWidth, Height = physHeight };
                var r = _cornerRects.TryGetValue(s.SlotNumber, out var cur) ? cur : ResolvePlacementRect(s);
                return new Views.LayoutEditorSlot { SlotNumber = s.SlotNumber, Label = s.Label, X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
            })
            .ToList();

        var wasBuiltIn = SelectedProfile.IsBuiltIn;
        ApplyEditedSlots(monitor, items);
        Log.Info($"Adjusted slot {position} by drag" + (wasBuiltIn ? " (saved to a new custom profile)." : "."));
    }

    // A drag/resize just began. MaintainCornerOverlays' hover-hit-test loop is gated off entirely
    // for the duration (TileSurfaceWindow.IsDragging) -- which means it will NOT notice "the cursor
    // left the peeked tile" once dragging starts, so a Peek-style (real-window swap) hover-peek that
    // was already mid-flight would otherwise get stuck active for the whole drag. Revert it now,
    // through the same path used when the cursor actually leaves a tile.
    private void OnCornerTileDragStarted(int position)
    {
        _hoverPeekTimer.Stop();
        _pendingHoverPosition = -1;
        if (_cursorOverPosition >= 0)
        {
            OnCornerTileHoverLeft(_cursorOverPosition);
            _cursorOverPosition = -1;
        }
    }

    // Cheap, live-only visual feedback for every mouse-move during a drag/resize -- moves the pill
    // label to follow the tile in real time. No persistence here (see OnCornerTileRectChanged for
    // that, fired once on mouse-up).
    private void OnCornerTileDragging(int position, int physX, int physY, int physWidth, int physHeight)
    {
        _labelSurface?.MovePill(position, new WindowRect { X = physX, Y = physY, Width = physWidth, Height = physHeight });
    }

    private void OnCornerTileHovered(int position)
    {
        if (!_settings.HoverPreviewEnabled) return;

        _pendingHoverPosition = position;

        var delay = _settings.HoverPreviewDelayMs;
        if (delay <= 0)
        {
            ExecuteHoverPeek(position);
            return;
        }

        _hoverPeekTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _hoverPeekTimer.Stop();
        _hoverPeekTimer.Start();
    }

    internal void OnHoverPeekTimerTick(object? sender, EventArgs e)
    {
        _hoverPeekTimer.Stop();
        if (_pendingHoverPosition < 0) return;
        ExecuteHoverPeek(_pendingHoverPosition);
    }

    private void ExecuteHoverPeek(int position)
    {
        if (SelectedProfile is null) return;

        // Zoom style: magnify the preview thumbnail in place. Real windows are never touched, so
        // none of the peek-swap state below applies.
        if (_settings.HoverPreviewStyle.Equals("Zoom", StringComparison.OrdinalIgnoreCase))
        {
            if (_tileSurface is not null) _tileSurface.ZoomAnchor = ResolveZoomAnchor(OccupantAtPosition(position));
            _tileSurface?.ZoomTile(position, Math.Clamp(_settings.HoverZoomFactor, 1.5, 4.0));
            // The magnified tile can now grow over master's screen area (no longer geometrically
            // clamped away from it) -- reassert topmost right away so the enlarged DWM thumbnail wins
            // the compositing there instead of relying on whatever z-order state happened to be
            // current already.
            ReassertOwnOverlaySurfaces();
            return;
        }

        var seat = OccupantAtPosition(position);

        // Focus-only seats never swap into master, so hover-peek (which stages that swap) is a no-op.
        if (Seat(seat)?.FocusOnlyNoSwap == true) return;

        var seatGroup = FindGroupForSeat(seat) ?? EffectiveGroups().FirstOrDefault();
        if (seatGroup is null) return;
        var groupId = seatGroup.GroupId;
        var groupCenter = CenterSlotForGroup(seatGroup);

        if (seat == _centeredSeatByGroup.GetValueOrDefault(groupId, 0)) return;

        var window = FindSeatWindow(seat);
        if (window is null) return;

        var centerSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == groupCenter);
        if (centerSlot is null) return;
        var masterRect = ResolveMasterRect(centerSlot);
        var parkRect = ResolveParkRect(masterRect);

        // Gate: don't fire while cursor is already over the master's own on-screen area. That area
        // is its preview tile when it has one (equal-cell layouts like Grid, where the real window
        // now runs at full resolution off in `masterRect` -- see ResolveMasterRect), otherwise the
        // master's own visible rect (dominant-master layouts like Center Master).
        var centerVisibleRect = _cornerRects.TryGetValue(groupCenter, out var selfTileRect) ? selfTileRect : masterRect;
        if (Utilities.Win32Native.GetCursorPos(out var gateCur) &&
            gateCur.X >= centerVisibleRect.X && gateCur.X < centerVisibleRect.X + centerVisibleRect.Width &&
            gateCur.Y >= centerVisibleRect.Y && gateCur.Y < centerVisibleRect.Y + centerVisibleRect.Height)
            return;

        var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(groupId, 0);
        var centeredWindow = FindSeatWindow(centeredSeat);

        RevertPeekSwap();

        // The peeked seat's real window always lives parked off-screen (never at the tile rect —
        // the tile is only ever a thumbnail), so the swap is master <-> park, not master <-> tile.
        try
        {
            if (centeredWindow is not null)
                _windowService.SwapWindowPositions(
                    window.Handle, masterRect.X, masterRect.Y,
                    centeredWindow.Handle, parkRect.X, parkRect.Y);
            else
                _windowService.MoveResizeWindow(window.Handle, masterRect);
        }
        catch (Exception ex) { Log.Error($"Hover peek swap failed: {ex.Message}"); return; }

        // The corner tile keeps rendering the same live thumbnail regardless (it captures by
        // window handle, not by physical position), so no overlay update is needed here. The
        // z-order DOES need reasserting though -- same reasoning as CenterSeatInGroup: moving a
        // real window (even with SWP_NOZORDER/SWP_NOACTIVATE) can still nudge it up a band, which
        // used to leave the master window sitting above the always-topmost overlay surfaces after
        // every hover-peek. This was the missing half of that fix -- CenterSeatInGroup reasserts,
        // ExecuteHoverPeek never did, and hover-peek fires far more often than click-to-center.
        // Use the cheap reassert, not RefreshCornerOverlayZOrder's full allow-list EnumWindows pass --
        // this fires on every tile transition during fast mouse movement, see ReassertOwnOverlaySurfaces.
        ReassertOwnOverlaySurfaces();

        _peekPosition = position;
        _peekSeat = seat;
        _peekCenteredSeat = centeredSeat;
        _peekGroupId = groupId;
        _peekMasterRect = masterRect;
        _peekParkRect = parkRect;
    }

    private void OnCornerTileHoverLeft(int position)
    {
        _hoverPeekTimer.Stop();
        _pendingHoverPosition = -1;
        _tileSurface?.ClearZoom();
        RevertPeekSwap();
    }

    private void RevertPeekSwap()
    {
        if (_peekPosition < 0) return;

        var peekSeat = _peekSeat;
        var centeredSeat = _peekCenteredSeat;
        var mr = _peekMasterRect;
        var pr = _peekParkRect;
        ClearPeekState();

        if (mr is null || pr is null) return;

        var peekWindow = FindSeatWindow(peekSeat);
        var centeredWindow = FindSeatWindow(centeredSeat);

        try
        {
            if (peekWindow is not null && centeredWindow is not null)
                _windowService.SwapWindowPositions(
                    centeredWindow.Handle, mr.X, mr.Y,
                    peekWindow.Handle, pr.X, pr.Y);
            else
            {
                if (centeredWindow is not null)
                    _windowService.MoveResizeWindow(centeredWindow.Handle, mr);
                if (peekWindow is not null)
                    _windowService.MoveResizeWindow(peekWindow.Handle, pr);
            }
        }
        catch (Exception ex) { Log.Error($"Hover peek revert failed: {ex.Message}"); }

        // Symmetric with the reassert in ExecuteHoverPeek -- the revert move can nudge z-order too.
        // Same cheap-vs-full reasoning as there: this can fire just as often.
        ReassertOwnOverlaySurfaces();
    }

    private void ClearPeekState()
    {
        _peekPosition = -1;
        _peekSeat = -1;
        _peekCenteredSeat = -1;
        _peekGroupId = null;
        _peekMasterRect = null;
        _peekParkRect = null;
    }

    // -- Per-tile updates --------------------------------------------------------

    internal void UpdateCornerOverlay(int position, nint sourceHwnd)
    {
        if (_tileSurface is null || !_cornerRects.ContainsKey(position)) return;
        _tileSurface.SetSource(position, sourceHwnd);
        _cornerSourceHandles[position] = sourceHwnd;
    }

    private void RefreshPositionPill(int position)
    {
        if (_labelSurface is null || !_cornerRects.ContainsKey(position)) return;
        var seat = OccupantAtPosition(position);
        var live = FindSeatWindow(seat) is not null;
        var sys = live ? SeatSystemName(seat) : "";
        _labelSurface.SetPillContent(position,
            live ? PillTextForPosition(position) : OfflinePillText(seat),
            SeatPortrait(seat), sys, SystemColorHex(sys));
        var isMasterPosition = IsGroupCenterPosition(position);
        var (family, size, color) = ResolveLabelFont(seat, isMasterPosition);
        var (bold, italic, dropShadow, outline, opacity) = ResolveLabelStyle(seat, isMasterPosition);
        _labelSurface.SetPillAppearance(position, family, size, color, bold, italic, dropShadow, outline, opacity);
    }

    private void RefreshGroupCenterPill(SwapGroup group)
    {
        if (_labelSurface is null) return;
        var groupCenter = CenterSlotForGroup(group);
        var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(group.GroupId, 0);
        var centerSys = SeatSystemName(centeredSeat);
        _labelSurface.SetPillContent(groupCenter, CenterPillTextForGroup(group.GroupId), SeatPortrait(centeredSeat),
            centerSys, SystemColorHex(centerSys));
        var (family, size, color) = ResolveLabelFont(centeredSeat, isMaster: true);
        var (bold, italic, dropShadow, outline, opacity) = ResolveLabelStyle(centeredSeat, isMaster: true);
        _labelSurface.SetPillAppearance(groupCenter, family, size, color, bold, italic, dropShadow, outline, opacity);
    }

    internal void RefreshAllPills()
    {
        foreach (var position in _cornerRects.Keys) RefreshPositionPill(position);
        foreach (var group in EffectiveGroups()) RefreshGroupCenterPill(group);
    }

    // True when an EVE client -- or EveDeck itself -- is the foreground app; drives whether the corner
    // previews sit on top (covering other apps) or sink to the bottom of the z-order.
    private bool IsEveOrEwcForeground()
    {
        var fg = _windowService.GetForegroundWindowHandle();
        if (fg == 0) return false;
        if (Windows.Any(w => w.Handle == fg)) return true;

        // The foreground-change WinEvent hook is registered inside MainWindow's own constructor
        // (HotkeyService.RegisterAll) and can fire before WPF has assigned Application.MainWindow --
        // WindowInteropHelper's ctor throws ArgumentNullException on a null window, which crashed the
        // whole process on startup whenever a real foreground switch happened early (i.e. whenever
        // EVE clients already existed to switch between). Treat "no main window yet" as simply "not
        // EveDeck's window" rather than dereferencing it.
        var mainWindow = System.Windows.Application.Current?.MainWindow;
        return mainWindow is not null && fg == new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
    }

    internal void RefreshCornerOverlayZOrder()
    {
        if (_tileSurface is null) return;
        ApplySurfaceZOrder();
    }

    // Cheap subset of ApplySurfaceZOrder: just the two surfaces this app itself owns (SetWindowPos
    // calls only). Deliberately skips BumpAllowedAppsAboveOverlaySurfaces, which does a full
    // EnumWindows + Process.GetProcessById(...) per visible top-level window -- fine for an
    // infrequent event (a click, the 250ms/2s periodic loop) but genuinely expensive to repeat many
    // times a second. Hover-peek fires on every tile transition during fast mouse movement, so it
    // needs this instead of RefreshCornerOverlayZOrder -- found live: the full version here caused
    // visible stutter during rapid corner-hopping in combat. The allow-list bump doesn't need
    // re-triggering per hover-peek anyway; MaintainCornerOverlays already refreshes it continuously
    // (every tick while EVE has focus) and the safety-net re-assert covers the rest within ~2s.
    private void ReassertOwnOverlaySurfaces()
    {
        _tileSurface?.SetZ();
        _labelSurface?.SetZ();
    }

    // The overlay (tiles + labels) always stays topmost, over EVE, EveDeck, and every other app
    // (browser, Discord, etc.) -- it's meant to be visible no matter what has focus. The label
    // surface is an owned window of the tile surface, so the window manager itself keeps labels
    // above tiles at all times -- there is nothing to re-assert per tick for THAT relationship.
    // This method itself is only called from event-driven triggers (surface creation, layout/swap
    // changes, the foreground WinEvent hook), never an unconditional per-tick timer.
    //
    // NOTE: EveDeck deliberately does NOT reorder the real EVE game windows, matching how EVE-O
    // Preview and EVE-APM behave. An attempt to raise the master client above the preview surface
    // (2026-07-21) made z-order visibly worse and was reverted -- do not reintroduce it.
    private void ApplySurfaceZOrder()
    {
        _tileSurface?.SetZ();
        _labelSurface?.SetZ();
        BumpAllowedAppsAboveOverlaySurfaces();
        if (_layoutEditorHwnd != 0) _windowService.SetWindowTopmost(_layoutEditorHwnd, true);
        BumpToastAboveEverything();
        // Same class of bug as the tile/label surfaces (see CenterSeatInGroup) -- a real EVE window
        // gaining focus during a swap can climb above the talker overlay too. It already self-heals
        // via a 1s timer (TalkerOverlayWindow.BringToTop), but re-asserting here too means it doesn't
        // wait up to a full second to recover right after a swap.
        _talkerWindow?.BringToTop();
    }

    // Re-asserts the toast window at the very top of the topmost band. Must run AFTER the surfaces'
    // own SetZ and the allow-list bump: the topmost band is ordered by whoever asserted LAST, not by
    // any fixed priority, so anything bumped after the toast would land on top of it.
    //
    // Gated on HasVisibleToasts so this only runs during the ~5s a toast is actually on screen. An
    // unconditional re-assert here would be the same mistake that made Mumble blink over Waterfox 4x
    // a second (see MaintainCornerOverlays) -- there is nothing to keep on top when no toast is up.
    private void BumpToastAboveEverything()
    {
        if (_toastHwnd == 0 || !_settings.ToastsAboveOverlays) return;
        if (_toastWindow?.HasVisibleToasts != true) return;
        _windowService.SetWindowTopmost(_toastHwnd, true);
    }

    // Shows a toast notification (chat keyword / game event / PI alerts) over the game, creating the
    // toast surface on first use. Deliberately independent of CornerOverlaysLive -- chat/game log
    // watching runs regardless of whether corner overlays are enabled.
    //
    // `seat`, when known, gives the card the seat's character portrait as its avatar and makes it
    // clickable: clicking centers that seat, exactly like clicking its preview tile.
    internal void ShowToast(string title, string message, string accentHex, SlotAssignment? seat = null)
    {
        if (EnsureToastWindow() is not { } window) return;
        window.ShowToast(title, message, accentHex, SeatAvatar(seat), SeatClickAction(seat));
        AssertToastZOrder();
        MirrorToNativeNotificationCenter(title, message);
    }

    // Multi-line variant: each alert becomes its own readable row instead of a newline-joined blob.
    internal void ShowToast(string title, IReadOnlyList<Views.ToastLine> lines, string accentHex, SlotAssignment? seat = null)
    {
        if (lines.Count == 0) return;
        if (EnsureToastWindow() is not { } window) return;
        window.ShowToast(title, lines, accentHex, SeatAvatar(seat), SeatClickAction(seat));
        AssertToastZOrder();
        MirrorToNativeNotificationCenter(title, string.Join(" | ", lines.Select(l => l.Primary)));
    }

    // Grouped variant: rows clustered under per-group headers -- see RaisePiAlerts (grouped per character).
    internal void ShowToast(string title, IReadOnlyList<Views.ToastGroup> groups, string accentHex, SlotAssignment? seat = null)
    {
        if (groups.Count == 0) return;
        if (EnsureToastWindow() is not { } window) return;
        window.ShowToast(title, groups, accentHex, SeatAvatar(seat), SeatClickAction(seat));
        AssertToastZOrder();
        MirrorToNativeNotificationCenter(title, string.Join(" | ", groups.SelectMany(g => g.Lines.Select(l => $"{g.Header}: {l.Primary}"))));
    }

    private Views.ToastNotificationWindow? EnsureToastWindow()
    {
        if (_toastWindow is not null) return _toastWindow;

        var monitor = Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
        if (monitor is null) return null;
        var dpiScale = monitor.DpiX / 96.0;
        var anchor = ParseToastAnchor(_settings.ToastPosition);
        // WORK area, not full Bounds -- Bounds includes the strip behind the taskbar, which is
        // exactly the strip a Bottom* anchor needs to clear to land "above the system clock".
        _toastWindow = new Views.ToastNotificationWindow(
            monitor.WorkArea.X, monitor.WorkArea.Y, monitor.WorkArea.Width, monitor.WorkArea.Height, dpiScale, anchor);
        _toastWindow.Show();
        _toastHwnd = _toastWindow.Handle;
        return _toastWindow;
    }

    private static Views.ToastAnchor ParseToastAnchor(string value) => value switch
    {
        "TopLeft" => Views.ToastAnchor.TopLeft,
        "TopCenter" => Views.ToastAnchor.TopCenter,
        "TopRight" => Views.ToastAnchor.TopRight,
        "BottomLeft" => Views.ToastAnchor.BottomLeft,
        "BottomCenter" => Views.ToastAnchor.BottomCenter,
        _ => Views.ToastAnchor.BottomRight,
    };

    // Best-effort mirror into the real Windows Notification Center so alerts are still reviewable
    // (from clicking the system clock) after EveDeck's own popup has faded. SuppressPopup means
    // Windows never shows its own banner for these -- EveDeck's styled popup stays the only visible
    // one. See NativeNotificationService's own doc comment for why this is wrapped this defensively.
    private void MirrorToNativeNotificationCenter(string title, string message, string? argument = null)
    {
        if (!_settings.NativeNotificationCenterEnabled) return;
        Services.NativeNotificationService.Show(title, message, argument);
    }

    private static System.Windows.Media.ImageSource? SeatAvatar(SlotAssignment? seat) => seat?.RunningPortrait?.Image;

    private Action? SeatClickAction(SlotAssignment? seat)
        => seat is null ? null : () => CenterSeat(seat.SlotNumber);

    // Re-assert every time a toast is shown, not just on window creation: a swap, a foreground
    // change, or the per-tick allow-list bump may all have re-ordered the topmost band since the
    // last one.
    private void AssertToastZOrder()
    {
        if (CornerOverlaysLive) ApplySurfaceZOrder();
        else BumpToastAboveEverything();
    }

    // Position (corner id, or a group's center slot number) that `seat` currently occupies on the
    // overlay -- the reverse of OccupantAtPosition. -1 when the seat isn't currently placed (corner
    // overlays off, or the profile has no matching slot).
    private int CurrentPositionForSeat(int seat)
    {
        if (!CornerOverlaysLive) return -1;
        foreach (var position in _cornerRects.Keys)
            if (OccupantAtPosition(position) == seat) return position;
        return -1;
    }

    // Pulses a red glow around `seat`'s current tile/master rect for ~2s -- the on-overlay
    // counterpart to FlashSeatAlert, reserved for FlashOnTile game events (combat by default) so it
    // renders over the game itself, not just the app's own seat list.
    internal void TriggerCombatGlow(SlotAssignment seat)
    {
        if (_labelSurface is null) return;
        var position = CurrentPositionForSeat(seat.SlotNumber);
        if (position < 0 || !_cornerRects.TryGetValue(position, out var rect)) return;

        _labelSurface.SetAlertGlow(position, rect, "#EF4444", _settings.AbyssModeEnabled);

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _labelSurface?.ClearAlertGlow(position);
        };
        timer.Start();
    }

    // Allow-listed apps (Options tab) that should stay visually above our overlay surfaces even
    // while an EVE client has focus -- e.g. a Mumble or RIFT window docked in a screen corner.
    public ObservableCollection<OverlayAllowedApp> OverlayAllowedApps => _settings.OverlayAllowedApps;

    private void BumpAllowedAppsAboveOverlaySurfaces()
    {
        var names = _settings.OverlayAllowedApps
            .Where(a => a.Enabled && !string.IsNullOrWhiteSpace(a.ProcessName))
            .Select(a => a.ProcessName.Trim())
            .ToList();
        if (names.Count == 0) return;
        _windowService.BumpMatchingProcessesAboveOverlay(names);
    }

    private void AddOverlayAllowedApp()
    {
        _settings.OverlayAllowedApps.Add(new OverlayAllowedApp { ProcessName = "" });
        Save();
    }

    private void RemoveOverlayAllowedApp(object? parameter)
    {
        if (parameter is not OverlayAllowedApp app) return;
        _settings.OverlayAllowedApps.Remove(app);
        Save();
    }

    // Non-EVE apps (Previews section) whose windows should also show up as detected/assignable
    // windows, so one can be assigned to a slot and previewed in a corner tile like any EVE client.
    public ObservableCollection<PreviewableApp> PreviewableApps => _settings.PreviewableApps;

    private void AddPreviewableApp()
    {
        _settings.PreviewableApps.Add(new PreviewableApp { ProcessName = "" });
        Save();
    }

    private void RemovePreviewableApp(object? parameter)
    {
        if (parameter is not PreviewableApp app) return;
        _settings.PreviewableApps.Remove(app);
        Save();
    }

    // -- Hide previews while EVE isn't the foreground app -------------------------
    // EVE-O Preview's HideThumbnailsOnLostFocus, and the reason EveDeck does not need to reorder
    // real game windows to "let apps cover the previews": alt-tab to a browser and the overlay simply
    // gets out of the way. The surfaces are hidden with SW_HIDE rather than torn down, so the DWM
    // thumbnail registrations survive and coming back is instant with no re-register blink (see
    // project-thumbnail-random-refresh for why re-registering is something to avoid).
    //
    // The delay exists because the foreground briefly leaves EVE during a seat swap and while dialogs
    // open; hiding instantly would flash the whole overlay off and back on during normal play.
    private void UpdateFocusLossHiding(bool eveOrEwcForeground)
    {
        if (!_settings.HidePreviewsOnFocusLoss)
        {
            if (_previewsHiddenByFocusLoss) SetOverlaySurfacesVisible(true);
            _focusLostSince = null;
            return;
        }

        if (eveOrEwcForeground)
        {
            _focusLostSince = null;
            if (_previewsHiddenByFocusLoss) SetOverlaySurfacesVisible(true);
            return;
        }

        if (_previewsHiddenByFocusLoss) return;
        _focusLostSince ??= DateTime.UtcNow;
        var delay = Math.Max(0, _settings.HidePreviewsOnFocusLossDelaySeconds);
        if ((DateTime.UtcNow - _focusLostSince.Value).TotalSeconds >= delay)
            SetOverlaySurfacesVisible(false);
    }

    private void SetOverlaySurfacesVisible(bool visible)
    {
        _previewsHiddenByFocusLoss = !visible;
        var cmd = visible ? Utilities.Win32Native.SwShowNoActivate : Utilities.Win32Native.SwHide;

        if (_tileSurface is { IsHandleCreated: true } tiles)
            Utilities.Win32Native.ShowWindow(tiles.Handle, cmd);
        if (_labelSurface is { } labels && labels.Handle != 0)
            Utilities.Win32Native.ShowWindow(labels.Handle, cmd);

        // Re-showing puts both surfaces back at the bottom of the z-order, so they need re-asserting
        // topmost or they come back buried behind the EVE client that just regained focus.
        if (visible) ReassertOwnOverlaySurfaces();
    }

    // -- Per-tick upkeep ---------------------------------------------------------

    internal void MaintainCornerOverlays()
    {
        if (_tileSurface is null) return;

        if ((DateTime.UtcNow - _lastZOrderSafetyNet).TotalSeconds >= 2)
        {
            _lastZOrderSafetyNet = DateTime.UtcNow;
            ApplySurfaceZOrder();
        }

        if (_settings.OfflineOverlayTimeoutSeconds > 0 && !Assignments.Any(a => FindAssignedWindows(a).Any()))
        {
            _allSeatsOfflineSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _allSeatsOfflineSince.Value).TotalSeconds >= _settings.OfflineOverlayTimeoutSeconds)
            {
                Log.Info("All seats have been offline; tearing down the corner overlay.");
                StopCornerOverlays();
                _allSeatsOfflineSince = null;
                return;
            }
        }
        else
        {
            _allSeatsOfflineSince = null;
        }

        // Maintain how long each seat has been continuously offline (for OfflinePillText's
        // per-seat hide timeout). Single per-tick update, mirrors the whole-overlay
        // _allSeatsOfflineSince pattern above but per seat; OfflinePillText only reads this.
        foreach (var assignment in Assignments)
        {
            if (FindAssignedWindows(assignment).Any())
                _seatOfflineSince.Remove(assignment.SlotNumber);
            else if (!_seatOfflineSince.ContainsKey(assignment.SlotNumber))
                _seatOfflineSince[assignment.SlotNumber] = DateTime.UtcNow;
        }

        var eveOrEwcFg = IsEveOrEwcForeground();

        // Runs before the z-order/hover work below: while the overlay is hidden there is nothing to
        // keep on top and no tile for the cursor to be over.
        UpdateFocusLossHiding(eveOrEwcFg);
        if (_previewsHiddenByFocusLoss) return;

        // The overlay itself is always topmost now (ApplySurfaceZOrder, called on creation and from
        // event-driven triggers) -- no per-tick re-assertion of OUR OWN HWND_TOPMOST here, that churn
        // was the historical source of every "labels flicker" report. The allow-list bump used to run
        // unconditionally every tick regardless of focus -- observed live fighting Waterfox for the
        // topmost slot 4x/second even while just browsing with no EVE client focused at all (Waterfox
        // apparently reclaims front-of-zorder periodically on its own; Firefox doesn't), visibly
        // blinking Mumble's window in and out. Gated to while EVE/EveDeck actually has focus, matching
        // the feature's own purpose ("stay above the overlay while gaming") -- a newly-launched
        // allow-listed app still gets caught immediately via the event-driven ApplySurfaceZOrder path
        // (foreground change hook) the moment the user alt-tabs back into EVE.
        if (eveOrEwcFg)
        {
            BumpAllowedAppsAboveOverlaySurfaces();
            // The bump above re-asserts every allow-listed app topmost, which would otherwise climb
            // straight over a toast showing in the same corner (Discord docked top-right, say). Put
            // the toast back on top afterwards -- self-gating on there actually being one on screen.
            BumpToastAboveEverything();
        }

        if (!eveOrEwcFg && (_peekPosition >= 0 || _pendingHoverPosition >= 0 || _cursorOverPosition >= 0))
        {
            if (_cursorOverPosition >= 0) OnCornerTileHoverLeft(_cursorOverPosition);
            _cursorOverPosition = -1;
        }

        // Suppressed while a tile is being dragged/resized -- moving the mouse across other tiles
        // mid-drag shouldn't also trigger hover-peek/zoom on them (see OnCornerTileDragStarted for
        // the matching cleanup of whatever was already active when the drag began).
        if (_settings.HoverPreviewEnabled && eveOrEwcFg && !_tileSurface.IsDragging && Utilities.Win32Native.GetCursorPos(out var cur))
        {
            int hitPos = -1;
            foreach (var (pos, r) in _cornerRects)
            {
                if (cur.X >= r.X && cur.X < r.X + r.Width && cur.Y >= r.Y && cur.Y < r.Y + r.Height)
                { hitPos = pos; break; }
            }
            if (hitPos < 0 && _peekPosition >= 0 && _cursorOverPosition >= 0 && _peekMasterRect is { } peekMr)
            {
                if (cur.X >= peekMr.X && cur.X < peekMr.X + peekMr.Width && cur.Y >= peekMr.Y && cur.Y < peekMr.Y + peekMr.Height)
                    hitPos = _cursorOverPosition;
            }
            if (hitPos != _cursorOverPosition)
            {
                if (_cursorOverPosition >= 0) OnCornerTileHoverLeft(_cursorOverPosition);
                if (hitPos >= 0) OnCornerTileHovered(hitPos);
                _cursorOverPosition = hitPos;
            }
        }

        // Source liveness: point each tile at its occupant's current window (clients relaunch with
        // new handles; a missing client hides the tile and shows an offline label until it returns).
        // With HideActiveSeatTile on, a tile whose occupant IS the foreground window is suppressed
        // (source 0 + blank pill) — it's already on screen full-size. The transition guard
        // (desired != lastHandle) keeps this a state change, not a per-tick churn.
        var fgHandle = _windowService.GetForegroundWindowHandle();
        foreach (var position in _cornerRects.Keys)
        {
            var seat = OccupantAtPosition(position);

            var preventPreview = Seat(seat)?.PreventPreview == true;
            _cornerPreventedState.TryGetValue(position, out var lastPrevented);
            if (preventPreview != lastPrevented)
            {
                _tileSurface.SetPreviewPrevented(position, preventPreview);
                _cornerPreventedState[position] = preventPreview;
            }

            var window = FindSeatWindow(seat);
            if (window is null)
            {
                if (_cornerSourceHandles.GetValueOrDefault(position) != 0)
                {
                    _tileSurface.SetSource(position, 0);
                    _labelSurface?.SetPillContent(position, OfflinePillText(seat), SeatPortrait(seat));
                    _cornerSourceHandles[position] = 0;
                }
                continue;
            }

            var hiddenAsActive = _settings.HideActiveSeatTile && window.Handle == fgHandle;
            // Title-only detection (no memory reading, no injection): EVE titles a client's window
            // plainly "EVE", with no " - Character Name" suffix, until a character has been selected
            // past the login/character-select screen -- reuses CharacterNameFromTitle's existing
            // "EVE - X" -> "X" parsing (MainWindowViewModel.Clients.cs) rather than a second rule, since
            // a title with no prefix match comes back unchanged, i.e. still literally "EVE".
            var hiddenAsLoginScreen = _settings.HidePreviewsAtLoginScreen && IsAtLoginScreen(window.Title);
            var desiredHandle = (hiddenAsActive || hiddenAsLoginScreen) ? 0 : window.Handle;
            _cornerSourceHandles.TryGetValue(position, out var lastHandle);
            if (desiredHandle != lastHandle)
            {
                _tileSurface.SetSource(position, desiredHandle);
                if (hiddenAsActive) _labelSurface?.SetPillContent(position, "", SeatPortrait(seat));
                else RefreshPositionPill(position);
                _cornerSourceHandles[position] = desiredHandle;
            }
        }

        foreach (var group in EffectiveGroups())
        {
            var groupCenter = CenterSlotForGroup(group);
            var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(group.GroupId, 0);
            var centeredWindow = FindSeatWindow(centeredSeat);
            _labelSurface?.SetPillContent(groupCenter,
                centeredWindow is null ? OfflinePillText(centeredSeat) : CenterPillTextForGroup(group.GroupId),
                SeatPortrait(centeredSeat));
        }
    }
}
