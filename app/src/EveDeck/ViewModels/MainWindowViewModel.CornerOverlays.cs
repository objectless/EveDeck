using System.Collections.ObjectModel;
using EveDeck.Models;
using EveDeck.Views;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    // -- Model A occupancy --------------------------------------------------------
    // Seats (SlotAssignment.SlotNumber) are FIXED accounts -- their Label (main character),
    // AssignedWindows, etc. never move. What changes is which seat occupies the centre rect and
    // which seat shows at each corner POSITION. A "position id" is the non-master profile slot
    // number whose rect defines that corner; positions are fixed for the session, occupants rotate.
    //
    // With multiple swap groups each group is an independent swap ring with its own centre slot and
    // master seat. All per-group occupancy is keyed by groupId (SwapGroup.GroupId or "__single__").
    // EffectiveGroups() synthesises a single all-slots group when no groups are defined so legacy
    // behaviour is preserved automatically.

    // ONE window hosts every tile thumbnail and ONE (owned by it, so the OS keeps it above) hosts
    // every label. Tiles + pills are keyed by POSITION id; centre pills are stored under the group's
    // centre slot number. With a single HWND per surface there is no per-tile z-order to maintain.
    private TileSurfaceWindow? _tileSurface;
    private LabelSurfaceWindow? _labelSurface;
    private bool _surfacesTopmost;
    private readonly Dictionary<int, nint> _cornerSourceHandles = new();
    private readonly Dictionary<int, WindowRect> _cornerRects = new();
    private int _cursorOverPosition = -1;

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
            Log.Warn("The master always sits in the centre. Drop it on the centre cell, or set a different master first.");
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

        // Surface bounds: the layout monitor, or the union of the slot rects as a fallback.
        int surfX, surfY, surfW, surfH;
        if (monitor is not null)
        {
            surfX = monitor.Bounds.X; surfY = monitor.Bounds.Y;
            surfW = monitor.Bounds.Width; surfH = monitor.Bounds.Height;
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

        var primaryCenter = groupCenterSlots.Values.FirstOrDefault(CenterSlotNumber);
        var primaryMasterRect = slotRects.GetValueOrDefault(primaryCenter) ?? new WindowRect();
        var primaryMasterCenterY = primaryMasterRect.Y + primaryMasterRect.Height / 2.0;

        _tileSurface = new TileSurfaceWindow(surfX, surfY, surfW, surfH);
        _tileSurface.TileClicked = OnCornerTileClicked;
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
            var pillAtTop = (rect.Y + rect.Height / 2.0) < primaryMasterCenterY;
            CreatePill(position, seat, rect, pillAtTop, centered: true,
                window is not null ? PillTextForPosition(position) : OfflinePillText(seat), SeatPortraitUrl(seat));

            _tileSurface.SetSource(position, window?.Handle ?? 0);
            _cornerSourceHandles[position] = window?.Handle ?? 0;
        }

        foreach (var group in EffectiveGroups())
        {
            var groupCenter = groupCenterSlots[group.GroupId];
            if (!slotRects.TryGetValue(groupCenter, out var masterRect)) continue;
            var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(group.GroupId, 0);
            var centerPillText = FindSeatWindow(centeredSeat) is not null ? CenterPillTextForGroup(group.GroupId) : OfflinePillText(centeredSeat);
            CreatePill(groupCenter, centeredSeat, masterRect, atTop: true, centered: false, centerPillText, SeatPortraitUrl(centeredSeat));

            var groupCenterSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == groupCenter);
            if (groupCenterSlot is not null && !HasDominantMasterSlot(groupCenterSlot))
            {
                // No dominant master area for this group (e.g. Grid family) -- the real master window
                // now runs at full resolution (see ResolveMasterRect) rather than being shrunk to this
                // cell, so the centre needs its own live preview tile just like every corner.
                var centerWindow = FindSeatWindow(centeredSeat);
                _tileSurface.AddTile(groupCenter, masterRect.X, masterRect.Y, masterRect.Width, masterRect.Height);
                _cornerRects[groupCenter] = masterRect;
                _tileSurface.SetSource(groupCenter, centerWindow?.Handle ?? 0);
                _cornerSourceHandles[groupCenter] = centerWindow?.Handle ?? 0;
            }
        }

        ApplySurfaceZOrder(IsEveOrEwcForeground());
        _frameTimer.Start();
    }

    private string SeatPortraitUrl(int seat) => Seat(seat)?.PortraitUrl ?? "";

    // Effective label font (family, WPF size, colour hex) for a seat: the seat's own overrides win,
    // else the global defaults. Public so the Options / per-seat font pickers can seed their dialog.
    public (string family, double size, string color) EffectiveSeatLabelFont(SlotAssignment seat)
    {
        var family = !string.IsNullOrWhiteSpace(seat.LabelFontFamily) ? seat.LabelFontFamily! : _settings.CornerOverlayLabelFontFamily;
        var size = seat.LabelFontSize ?? _settings.CornerOverlayLabelFontSize;
        var color = !string.IsNullOrWhiteSpace(seat.LabelColor) ? seat.LabelColor! : _settings.CornerOverlayLabelColor;
        return (family ?? "", size, color ?? "");
    }

    private (string family, double size, string color) ResolveLabelFont(int seat)
    {
        var s = Seat(seat);
        return s is null
            ? (_settings.CornerOverlayLabelFontFamily ?? "", _settings.CornerOverlayLabelFontSize, _settings.CornerOverlayLabelColor ?? "")
            : EffectiveSeatLabelFont(s);
    }

    private void CreatePill(int key, int seat, WindowRect rect, bool atTop, bool centered, string text, string portraitUrl)
    {
        if (_labelSurface is null) return;
        var (family, size, color) = ResolveLabelFont(seat);
        _labelSurface.SetPill(key, rect, atTop, centered, family, size, color);
        _labelSurface.SetPillContent(key, text, portraitUrl);
    }

    // -- Pill captions -----------------------------------------------------------

    private string PillTextForPosition(int position)
    {
        var occupant = OccupantAtPosition(position);
        var name = SeatLabel(occupant);
        var code = CornerCode(position);
        var text = _settings.CornerOverlayShowSlotNumber && !string.IsNullOrEmpty(code) ? $"{code} · {name}" : name;
        return AppendSystem(text, occupant);
    }

    // "Name · Jita" when the seat's current solar system is known (Local chatlog tracking) and the
    // option is on; the bare text otherwise.
    private string AppendSystem(string text, int seat)
    {
        var system = SeatSystemName(seat);
        return system.Length > 0 && text.Length > 0 ? $"{text} · {system}" : text;
    }

    // Label for a seat whose client window is gone — keeps the seat identifiable instead of a
    // blank tile ("offline badge"). Empty when the seat itself has no label to show.
    private string OfflinePillText(int seat)
    {
        var name = SeatLabel(seat);
        return name.Length > 0 ? $"{name} · offline" : "";
    }

    private string CornerCode(int position)
    {
        var profile = SelectedProfile;
        if (profile is null || profile.Slots.Count == 0) return position.ToString();

        var slots = profile.Slots;
        var minX = slots.Min(s => s.X);
        var minY = slots.Min(s => s.Y);
        var totalW = Math.Max(1, slots.Max(s => s.X + s.Width) - minX);
        var totalH = Math.Max(1, slots.Max(s => s.Y + s.Height) - minY);
        var slot = slots.FirstOrDefault(s => s.SlotNumber == position);
        return slot is null ? position.ToString() : GridCode(slot, minX, minY, totalW, totalH);
    }

    private string CenterPillTextForGroup(string groupId)
    {
        var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(groupId, 0);
        var name = SeatLabel(centeredSeat);
        var text = _settings.CornerOverlayShowSlotNumber ? $"Master · {name}" : name;
        return AppendSystem(text, centeredSeat);
    }

    // -- Stop / teardown ---------------------------------------------------------

    internal void StopCornerOverlays()
    {
        _hoverPeekTimer.Stop();
        _pendingHoverPosition = -1;
        RevertPeekSwap();
        _cursorOverPosition = -1;

        try { _labelSurface?.Close(); } catch { } // window may already be closed
        _labelSurface = null;
        try { _tileSurface?.Close(); } catch { } // window may already be closed
        _tileSurface = null;
        _surfacesTopmost = false;
        _cornerSourceHandles.Clear();
        _cornerRects.Clear();

        if (!_settings.ActiveFrameEnabled) _frameTimer.Stop();
    }

    // -- Centre a seat -----------------------------------------------------------

    internal void CenterSeat(int seat)
    {
        if (SelectedProfile is null) return;

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
        if (masterSlot is null) { Log.Warn("Corner mode requires a layout with a centre slot."); return; }

        if (_centeredSeatByGroup.Count == 0) ResetCornerOccupancy();

        var currentCenteredSeat = _centeredSeatByGroup.GetValueOrDefault(groupId, 0);
        if (seat == currentCenteredSeat)
        {
            var already = FindSeatWindow(seat);
            if (already is not null) { try { _windowService.FocusWindow(already.Handle); } catch { /* best-effort focus */ } }
            Log.Info($"Seat {seat} ({SeatLabel(seat)}) is already centred.");
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
            catch (Exception ex) { Log.Error($"Centre move failed: {ex.Message}"); return; }
        }
        else
        {
            try { if (incoming is not null) _windowService.MoveResizeWindow(incoming.Handle, masterRect); }
            catch (Exception ex) { Log.Error($"Could not centre seat {seat}: {ex.Message}"); }
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

        RefreshCornerOverlayZOrder();
        if (incoming is not null) { try { _windowService.FocusWindow(incoming.Handle); } catch { /* best-effort focus */ } }

        ScheduleAutoSave();
        Log.Info($"Centred seat {seat} ({SeatLabel(seat)}) in group '{group.Name}'; seat {outgoingSeat} ({SeatLabel(outgoingSeat)}) returned to its home corner.");
    }

    private void CenterSeatFlatInGroup(SwapGroup group, int seat)
    {
        var groupId = group.GroupId;
        var groupCenter = CenterSlotForGroup(group);
        var centerSlot = SelectedProfile!.Slots.FirstOrDefault(s => s.SlotNumber == groupCenter);
        if (centerSlot is null) { Log.Warn("Grid swap requires a layout with a centre slot."); return; }

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

        UpdatePositionCodes();
        ScheduleAutoSave();
        Log.Info($"Centred seat {seat} ({SeatLabel(seat)}) in group '{group.Name}'; seat {outgoingSeat} ({SeatLabel(outgoingSeat)}) returned to its home corner.");
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
            _tileSurface?.ZoomTile(position, Math.Clamp(_settings.HoverZoomFactor, 1.5, 4.0));
            return;
        }

        var seat = OccupantAtPosition(position);

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
        // window handle, not by physical position), so no overlay update is needed here.

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
        _labelSurface.SetPillContent(position,
            FindSeatWindow(seat) is not null ? PillTextForPosition(position) : OfflinePillText(seat),
            SeatPortraitUrl(seat));
        var (family, size, color) = ResolveLabelFont(seat);
        _labelSurface.SetPillAppearance(position, family, size, color);
    }

    private void RefreshGroupCenterPill(SwapGroup group)
    {
        if (_labelSurface is null) return;
        var groupCenter = CenterSlotForGroup(group);
        var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(group.GroupId, 0);
        _labelSurface.SetPillContent(groupCenter, CenterPillTextForGroup(group.GroupId), SeatPortraitUrl(centeredSeat));
        var (family, size, color) = ResolveLabelFont(centeredSeat);
        _labelSurface.SetPillAppearance(groupCenter, family, size, color);
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
        ApplySurfaceZOrder(IsEveOrEwcForeground());
    }

    // The ONLY z-order management left in the overlay subsystem: two windows, moved between the
    // topmost band (EVE/EveDeck focused) and the bottom (anything else focused), on focus
    // transitions. The label surface is an owned window of the tile surface, so the window manager
    // itself keeps labels above tiles at all times -- there is nothing to re-assert per tick.
    private void ApplySurfaceZOrder(bool topmost)
    {
        _surfacesTopmost = topmost;
        _tileSurface?.SetZ(topmost);
        _labelSurface?.SetZ(topmost);
        if (topmost) BumpAllowedAppsAboveOverlaySurfaces();
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

    // -- Per-tick upkeep ---------------------------------------------------------

    internal void MaintainCornerOverlays()
    {
        if (_tileSurface is null) return;

        var eveOrEwcFg = IsEveOrEwcForeground();
        // Focus gating is event-driven (the foreground WinEvent hook in MainWindow); this is only a
        // fallback for a missed transition. No unconditional per-tick SetWindowPos calls on OUR OWN
        // surfaces -- that churn was the historical source of every "labels flicker" report. The
        // allow-list bump below only touches OTHER processes' windows, not ours, so it's safe to
        // re-run each tick while already topmost (catches an allow-listed app launched mid-session).
        if (eveOrEwcFg != _surfacesTopmost) ApplySurfaceZOrder(eveOrEwcFg);
        else if (_surfacesTopmost) BumpAllowedAppsAboveOverlaySurfaces();

        if (!eveOrEwcFg && (_peekPosition >= 0 || _pendingHoverPosition >= 0 || _cursorOverPosition >= 0))
        {
            if (_cursorOverPosition >= 0) OnCornerTileHoverLeft(_cursorOverPosition);
            _cursorOverPosition = -1;
        }

        if (_settings.HoverPreviewEnabled && eveOrEwcFg && Utilities.Win32Native.GetCursorPos(out var cur))
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
            var window = FindSeatWindow(seat);
            if (window is null)
            {
                if (_cornerSourceHandles.GetValueOrDefault(position) != 0)
                {
                    _tileSurface.SetSource(position, 0);
                    _labelSurface?.SetPillContent(position, OfflinePillText(seat), SeatPortraitUrl(seat));
                    _cornerSourceHandles[position] = 0;
                }
                continue;
            }

            var hiddenAsActive = _settings.HideActiveSeatTile && window.Handle == fgHandle;
            var desiredHandle = hiddenAsActive ? 0 : window.Handle;
            _cornerSourceHandles.TryGetValue(position, out var lastHandle);
            if (desiredHandle != lastHandle)
            {
                _tileSurface.SetSource(position, desiredHandle);
                if (hiddenAsActive) _labelSurface?.SetPillContent(position, "", SeatPortraitUrl(seat));
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
                SeatPortraitUrl(centeredSeat));
        }
    }
}
