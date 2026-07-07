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

    // Tiles + pills are keyed by POSITION id. Centre pills are stored under the group's centre slot number.
    private readonly Dictionary<int, CornerOverlayWindow> _cornerOverlays = new();
    private readonly Dictionary<int, PillOverlay> _pills = new();
    private readonly Dictionary<int, nint> _cornerSourceHandles = new();
    private readonly Dictionary<int, WindowRect> _cornerRects = new();
    private int _cursorOverPosition = -1;

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
        var overlaysLive = _cornerOverlays.Count > 0;
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

        var groupCenterSlots = EffectiveGroups()
            .ToDictionary(g => g.GroupId, g => CenterSlotForGroup(g));

        var primaryCenter = groupCenterSlots.Values.FirstOrDefault(CenterSlotNumber);
        var primaryMasterSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == primaryCenter);
        var primaryMasterRect = primaryMasterSlot is not null ? ResolvePlacementRect(primaryMasterSlot) : new WindowRect();
        var primaryMasterCenterY = primaryMasterRect.Y + primaryMasterRect.Height / 2.0;

        var allGroupCenterSlotNums = groupCenterSlots.Values.ToHashSet();
        foreach (var slot in SelectedProfile.Slots.Where(s => !allGroupCenterSlotNums.Contains(s.SlotNumber)))
        {
            var position = slot.SlotNumber;
            var rect = ResolvePlacementRect(slot);

            var overlay = new CornerOverlayWindow(rect.X, rect.Y, rect.Width, rect.Height, dpiScale, _settings);
            overlay.Clicked = () => OnCornerTileClicked(position);
            overlay.Show();
            _cornerOverlays[position] = overlay;
            _cornerRects[position] = rect;

            var pillAtTop = (rect.Y + rect.Height / 2.0) < primaryMasterCenterY;
            CreatePill(position, OccupantAtPosition(position), rect, dpiScale, pillAtTop, PillTextForPosition(position),
                SeatPortraitUrl(OccupantAtPosition(position)), centered: true);

            var seat = OccupantAtPosition(position);
            var window = FindSeatWindow(seat);
            if (window is not null)
            {
                overlay.UpdateSource(window.Handle);
                _cornerSourceHandles[position] = window.Handle;
            }
            else
            {
                overlay.Visibility = System.Windows.Visibility.Hidden;
                if (_pills.TryGetValue(position, out var emptyPill)) emptyPill.SetText("");
                _cornerSourceHandles[position] = 0;
            }
        }

        foreach (var group in EffectiveGroups())
        {
            var groupCenter = groupCenterSlots[group.GroupId];
            var masterSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == groupCenter);
            if (masterSlot is null) continue;
            var masterRect = ResolvePlacementRect(masterSlot);
            var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(group.GroupId, 0);
            var centerPillText = FindSeatWindow(centeredSeat) is not null ? CenterPillTextForGroup(group.GroupId) : "";
            CreatePill(groupCenter, centeredSeat, masterRect, dpiScale, atTop: true, centerPillText, SeatPortraitUrl(centeredSeat));
        }

        if (_cornerOverlays.Count > 0) _frameTimer.Start();
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

    private void CreatePill(int key, int seat, WindowRect rect, double dpiScale, bool atTop, string text, string portraitUrl, bool centered = false)
    {
        if (!_settings.CornerOverlayShowLabel) return;
        var (family, size, color) = ResolveLabelFont(seat);
        var pill = new PillOverlay(rect.X, rect.Y, rect.Width, rect.Height, dpiScale, _settings, atTop, centered, family, size, color);
        pill.Show();
        pill.SetContent(text, portraitUrl);
        _pills[key] = pill;
    }

    // -- Pill captions -----------------------------------------------------------

    private string PillTextForPosition(int position)
    {
        var occupant = OccupantAtPosition(position);
        var name = SeatLabel(occupant);
        var code = CornerCode(position);
        return _settings.CornerOverlayShowSlotNumber && !string.IsNullOrEmpty(code) ? $"{code} · {name}" : name;
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
        return _settings.CornerOverlayShowSlotNumber ? $"Master · {name}" : name;
    }

    // -- Stop / teardown ---------------------------------------------------------

    internal void StopCornerOverlays()
    {
        _hoverPeekTimer.Stop();
        _pendingHoverPosition = -1;
        RevertPeekSwap();
        _cursorOverPosition = -1;

        foreach (var overlay in _cornerOverlays.Values)
        {
            try { overlay.Close(); } catch { } // window may already be closed
        }
        _cornerOverlays.Clear();
        _cornerSourceHandles.Clear();
        _cornerRects.Clear();

        foreach (var pill in _pills.Values)
        {
            try { pill.Close(); } catch { } // window may already be closed
        }
        _pills.Clear();

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
        var masterRect = ResolvePlacementRect(masterSlot);
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
        var masterRect = ResolvePlacementRect(centerSlot);
        var parkRect = ResolveParkRect(masterRect);

        // Gate: don't fire while cursor is already over the master rect.
        if (Utilities.Win32Native.GetCursorPos(out var gateCur) &&
            gateCur.X >= masterRect.X && gateCur.X < masterRect.X + masterRect.Width &&
            gateCur.Y >= masterRect.Y && gateCur.Y < masterRect.Y + masterRect.Height)
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
        if (_cornerOverlays.TryGetValue(position, out var overlay))
        {
            overlay.UpdateSource(sourceHwnd);
            _cornerSourceHandles[position] = sourceHwnd;
        }
    }

    private void RefreshPositionPill(int position)
    {
        if (_pills.TryGetValue(position, out var pill))
        {
            var seat = OccupantAtPosition(position);
            pill.SetContent(PillTextForPosition(position), SeatPortraitUrl(seat));
            var (family, size, color) = ResolveLabelFont(seat);
            pill.UpdateAppearance(family, size, color);
        }
    }

    private void RefreshGroupCenterPill(SwapGroup group)
    {
        var groupCenter = CenterSlotForGroup(group);
        var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(group.GroupId, 0);
        if (_pills.TryGetValue(groupCenter, out var pill))
        {
            pill.SetContent(CenterPillTextForGroup(group.GroupId), SeatPortraitUrl(centeredSeat));
            var (family, size, color) = ResolveLabelFont(centeredSeat);
            pill.UpdateAppearance(family, size, color);
        }
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
        var topmost = IsEveOrEwcForeground();
        foreach (var overlay in _cornerOverlays.Values)
            overlay.RefreshZOrder(topmost);
        if (topmost)
            foreach (var pill in _pills.Values) pill.BringToTop();
    }

    // -- Per-tick upkeep ---------------------------------------------------------

    internal void MaintainCornerOverlays()
    {
        if (_cornerOverlays.Count == 0) return;

        var eveOrEwcFg = IsEveOrEwcForeground();
        // Only sink pills here when losing focus. Raising them here too (unconditionally, every tick)
        // was pure churn while focused: the corner-tile loop below re-asserts HWND_TOPMOST on every
        // tile right after, burying whatever this raised, and the "keep pills above tiles" pass at the
        // bottom of this method raises them again -- a raise/bury/raise cycle every single 250ms tick,
        // forever, while EVE has focus. That constant self-inflicted z-order thrash is what read as
        // "labels flickering" with no trigger. The bottom pass already re-asserts pills above tiles
        // when focused, so this loop only needs to handle the sink-on-focus-loss case.
        if (!eveOrEwcFg)
            foreach (var pill in _pills.Values)
                pill.SetTopmost(false);

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

        foreach (var (position, overlay) in _cornerOverlays)
        {
            var seat = OccupantAtPosition(position);
            var window = FindSeatWindow(seat);
            if (window is null)
            {
                if (_cornerSourceHandles.GetValueOrDefault(position) != 0)
                {
                    overlay.SourceLost();
                    if (_pills.TryGetValue(position, out var lostPill)) lostPill.SetText("");
                    _cornerSourceHandles[position] = 0;
                }
                continue;
            }

            _cornerSourceHandles.TryGetValue(position, out var lastHandle);
            if (window.Handle != lastHandle)
            {
                overlay.UpdateSource(window.Handle);
                RefreshPositionPill(position);
                _cornerSourceHandles[position] = window.Handle;
            }

            overlay.RefreshZOrder(eveOrEwcFg);
        }

        foreach (var group in EffectiveGroups())
        {
            var groupCenter = CenterSlotForGroup(group);
            var centeredSeat = _centeredSeatByGroup.GetValueOrDefault(group.GroupId, 0);
            if (_pills.TryGetValue(groupCenter, out var masterPill))
            {
                var centeredWindow = FindSeatWindow(centeredSeat);
                masterPill.SetContent(
                    centeredWindow is null ? "" : CenterPillTextForGroup(group.GroupId),
                    SeatPortraitUrl(centeredSeat));
            }
        }

        // Tiles just went topmost (when focused); keep every name pill above them.
        if (eveOrEwcFg)
            foreach (var pill in _pills.Values) pill.BringToTop();
    }
}
