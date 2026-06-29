using EveWindowCommander.Models;
using EveWindowCommander.Views;

namespace EveWindowCommander.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ── Model A occupancy ──────────────────────────────────────────────────────
    // Seats (SlotAssignment.SlotNumber) are FIXED accounts — their Label (main character),
    // AssignedWindows, etc. never move. What changes is which seat occupies the centre rect and
    // which seat shows at each corner POSITION. A "position id" is the non-master profile slot
    // number whose rect defines that corner; positions are fixed for the session, occupants rotate.

    // Tiles + pills are keyed by POSITION id. The centre pill is stored under the master slot number.
    private readonly Dictionary<int, CornerOverlayWindow> _cornerOverlays = new();
    private readonly Dictionary<int, PillOverlay> _pills = new();
    private readonly Dictionary<int, nint> _cornerSourceHandles = new();   // position id -> currently shown source HWND
    private readonly Dictionary<int, WindowRect> _cornerRects = new();     // position id -> physical tile rect (for cursor polling)
    private int _cursorOverPosition = -1;                                   // position currently under the cursor (-1 = none)

    // Flyout card currently showing a corner client's enlarged preview. Null = no flyout active.
    private HoverFlyoutWindow? _hoverFlyout;
    // Corner position waiting for the debounce delay to fire. -1 = none pending.
    private int _pendingHoverPosition = -1;

    private int _centeredSeat;                                             // seat currently at the centre rect
    private readonly Dictionary<int, int> _cornerSeat = new();             // position id -> seat currently shown there

    // The baseline ("home") arrangement, captured by ResetCornerOccupancy. Every non-master seat has a
    // fixed home corner; the master seat has none (it lives in the centre). CenterSeat rebuilds the whole
    // corner arrangement from these each switch, so a character always returns to its OWN home corner
    // instead of drifting into whatever slot the last swap happened to vacate.
    private readonly Dictionary<int, int> _homeOccupant = new();           // home position id -> seat
    private readonly Dictionary<int, int> _homePosition = new();           // seat -> home position id

    // The at-rest "home" arrangement of corner positions → seats. Honours each profile slot's user-set
    // HomeSeat (drag a seat card onto a mini-map corner); any position left unset, or whose HomeSeat is
    // the master seat / an already-placed seat, falls back to the legacy rule (seat number == position)
    // and finally a deterministic zip of leftovers. The master seat is excluded — it lives in the centre.
    // Fully determined by the profile + master setting, so the mini-map editor and runtime never diverge.
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

        // 1. Honour explicit, valid home corners first.
        var openPositions = new List<int>();
        foreach (var p in cornerPositions)
        {
            var hs = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == p)?.HomeSeat;
            if (hs.HasValue && hs.Value != master && remaining.Remove(hs.Value))
                result[p] = hs.Value;
            else
                openPositions.Add(p);
        }

        // 2. Legacy identity (seat number == position) for any still-open corner.
        foreach (var p in openPositions.ToList())
        {
            if (remaining.Remove(p)) { result[p] = p; openPositions.Remove(p); }
        }

        // 3. Zip whatever is left.
        foreach (var (p, seat) in openPositions.Zip(remaining))
            result[p] = seat;

        return result;
    }

    // Reset occupancy to the baseline computed by ComputeHomeArrangement: the master SEAT sits in the
    // centre geometry; the remaining seats fill their (user-set or auto) home corners.
    internal void ResetCornerOccupancy()
    {
        _centeredSeat = ActiveMasterSeat;
        _cornerSeat.Clear();
        if (SelectedProfile is null) return;

        foreach (var (p, seat) in ComputeHomeArrangement())
            _cornerSeat[p] = seat;

        // Snapshot this baseline as each seat's fixed home corner.
        _homeOccupant.Clear();
        _homePosition.Clear();
        foreach (var (p, seat) in _cornerSeat)
        {
            _homeOccupant[p] = seat;
            _homePosition[seat] = p;
        }
    }

    // ── Mini-map editor: assign a seat's home corner / master ───────────────────

    // Drag-drop target from the Clients-tab mini-map. Drop a seat card on the CENTRE cell to make it
    // master; drop on a CORNER cell to pin that seat there (swapping with whoever currently homes that
    // corner so no corner is left empty). Per-profile (stored on LayoutSlot.HomeSeat).
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

        // Clean 2-way swap based on the current at-rest arrangement: the seat already homed at `position`
        // takes the dragged seat's old corner. Both written explicitly so the result is unambiguous.
        var arrangement = ComputeHomeArrangement();
        if (arrangement.GetValueOrDefault(position) == seat) return;   // already there
        var displaced = arrangement.GetValueOrDefault(position);       // seat currently homed at target
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

    // Re-baseline corner occupancy after the home map changed, refreshing the mini-map and (if a corner
    // overlay layout is live) the on-screen tiles/pills.
    private void ReapplyCornerHomes()
    {
        var overlaysLive = _cornerOverlays.Count > 0;
        ResetCornerOccupancy();
        UpdatePositionCodes();
        RebuildMiniMap();
        if (overlaysLive)
        {
            StopCornerOverlays();
            _cornerSeat.Clear();   // force StartCornerOverlays to rebuild from the new baseline
            StartCornerOverlays();
        }
    }

    // The home-based corner arrangement when `seat` is centred: every non-master seat sits in its own
    // home corner; the master seat fills the home corner of whoever is centred. Fully determined by
    // `seat` (no dependence on the current arrangement), so repeated switches never drift — a character
    // always lands back in its OWN corner. Returns null if the home map can't place this seat.
    private Dictionary<int, int>? BuildTargetOccupancy(int seat)
    {
        if (_homeOccupant.Count == 0) return null;
        var masterSeat = ActiveMasterSeat;

        var target = new Dictionary<int, int>(_homeOccupant);   // baseline: everyone in their home corner
        if (seat == masterSeat) return target;                  // re-centring the master seat = baseline

        if (!_homePosition.TryGetValue(seat, out var seatHome)) return null;
        target[seatHome] = masterSeat;                          // master seat fills the centred seat's corner
        return target;
    }

    // ── Seat lookups ───────────────────────────────────────────────────────────

    private SlotAssignment? Seat(int seat) => Assignments.FirstOrDefault(a => a.SlotNumber == seat);
    private EveWindowInfo? FindSeatWindow(int seat)
    {
        var a = Seat(seat);
        return a is null ? null : FindAssignedWindows(a).FirstOrDefault();
    }
    private string SeatLabel(int seat) => Seat(seat)?.Label ?? "";

    // ── Create / show ──────────────────────────────────────────────────────────

    internal void StartCornerOverlays()
    {
        StopCornerOverlays();

        if (!_settings.CornerOverlaysEnabled) return;
        if (SelectedProfile is null || !SelectedProfile.SupportsCornerGrid) return;

        EnsureValidMasterSeat();

        // Centre rect = the largest slot's geometry (never the drifting master-slot number).
        var center = CenterSlotNumber;
        var masterSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == center);
        if (masterSlot is null) return;

        if (_cornerSeat.Count == 0) ResetCornerOccupancy();
        UpdatePositionCodes();

        var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var dpiScale = monitor is null ? 1.0 : monitor.DpiX / 96.0;

        var masterRect = ResolvePlacementRect(masterSlot);
        var masterCenterY = masterRect.Y + masterRect.Height / 2.0;

        foreach (var slot in SelectedProfile.Slots.Where(s => s.SlotNumber != center))
        {
            var position = slot.SlotNumber;

            // Position tiles with the same resolver the windows use, so corners line up with the
            // real master/park geometry even when the monitor res differs from the preset.
            var rect = ResolvePlacementRect(slot);

            var overlay = new CornerOverlayWindow(rect.X, rect.Y, rect.Width, rect.Height, dpiScale, _settings);
            overlay.Clicked = () => OnCornerTileClicked(position);
            overlay.Show();
            _cornerOverlays[position] = overlay;
            _cornerRects[position] = rect;

            // Top tiles get their pill at the top edge (their bottom edge is hidden by the master).
            var pillAtTop = (rect.Y + rect.Height / 2.0) < masterCenterY;
            CreatePill(position, rect, dpiScale, pillAtTop, PillTextForPosition(position),
                SeatPortraitUrl(_cornerSeat.GetValueOrDefault(position, position)));

            var seat = _cornerSeat.GetValueOrDefault(position, position);
            var window = FindSeatWindow(seat);
            if (window is not null)
            {
                overlay.UpdateSource(window.Handle);
                _cornerSourceHandles[position] = window.Handle;
            }
            else
            {
                // No client running for this seat yet — hide tile and blank pill until one appears.
                overlay.Visibility = System.Windows.Visibility.Hidden;
                if (_pills.TryGetValue(position, out var emptyPill)) emptyPill.SetText("");
                _cornerSourceHandles[position] = 0;
            }
        }

        // Centre pill (stored under the centre-slot key — never collides with a corner position),
        // at the top of the master rect.
        var centerPillText = FindSeatWindow(_centeredSeat) is not null ? CenterPillText() : "";
        CreatePill(center, masterRect, dpiScale, atTop: true, centerPillText, SeatPortraitUrl(_centeredSeat));

        // The frame timer drives corner-overlay maintenance (Z-order + dead-source refresh).
        if (_cornerOverlays.Count > 0) _frameTimer.Start();
    }

    // Rounded portrait of the main character occupying a seat (empty when unlinked).
    private string SeatPortraitUrl(int seat) => Seat(seat)?.PortraitUrl ?? "";

    private void CreatePill(int key, WindowRect rect, double dpiScale, bool atTop, string text, string portraitUrl)
    {
        if (!_settings.CornerOverlayShowLabel) return;
        var pill = new PillOverlay(rect.X, rect.Y, rect.Width, rect.Height, dpiScale, _settings, atTop);
        pill.Show();
        pill.SetContent(text, portraitUrl);
        _pills[key] = pill;
    }

    // ── Pill captions ──────────────────────────────────────────────────────────

    // Corner pill: occupant's name, optionally prefixed by the (fixed) corner code (TL · Name).
    private string PillTextForPosition(int position)
    {
        var occupant = _cornerSeat.GetValueOrDefault(position, position);
        var name = SeatLabel(occupant);
        var code = CornerCode(position);   // geometric corner code (TL/TR/…), independent of the master-seat badge
        return _settings.CornerOverlayShowSlotNumber && !string.IsNullOrEmpty(code) ? $"{code} · {name}" : name;
    }

    // The geometric location code (TL/TR/BL/BR/…) of a corner position, derived from slot geometry.
    // Unlike SlotAssignment.PositionCode this never becomes "Master", so a corner whose home seat is the
    // master seat still shows its true corner label.
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

    // Centre pill: the centred seat's name, optionally prefixed with "Master".
    private string CenterPillText()
    {
        var name = SeatLabel(_centeredSeat);
        return _settings.CornerOverlayShowSlotNumber ? $"Master · {name}" : name;
    }

    // ── Stop / teardown ────────────────────────────────────────────────────────

    internal void StopCornerOverlays()
    {
        // Discard any pending or active flyout without side-effects.
        _hoverPeekTimer.Stop();
        _pendingHoverPosition = -1;
        CloseFlyout();
        _cursorOverPosition = -1;

        foreach (var overlay in _cornerOverlays.Values)
        {
            try { overlay.Close(); } catch { }
        }
        _cornerOverlays.Clear();
        _cornerSourceHandles.Clear();
        _cornerRects.Clear();

        foreach (var pill in _pills.Values)
        {
            try { pill.Close(); } catch { }
        }
        _pills.Clear();

        // NB: occupancy (_cornerSeat / _centeredSeat) is intentionally preserved so a rebuild
        // (label/WGC toggle) keeps the current arrangement; ApplyActiveProfile resets it.

        if (!_settings.ActiveFrameEnabled) _frameTimer.Stop();
    }

    // ── Centre a seat (the core fast-switch) ───────────────────────────────────

    // Bring SEAT's account/client to the centre rect; send the previously-centred seat to the corner
    // position SEAT vacated. Seats/labels never move — only window positions + tile sources change.
    internal void CenterSeat(int seat)
    {
        if (SelectedProfile is null) return;

        // Single/stacked layouts have no centre to swap into — just focus the seat's client.
        if (!SelectedProfile.SupportsCornerGrid)
        {
            FocusSlot(seat);
            return;
        }

        // Flat (tiled) grid: corner overlays disabled but the profile still grids. Physically swap the
        // centre client with the target's corner client so the same master↔character switch works here.
        if (!_settings.CornerOverlaysEnabled)
        {
            CenterSeatFlat(seat);
            return;
        }

        var masterSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == CenterSlotNumber);
        if (masterSlot is null) { Log.Warn("Corner mode requires a layout with a centre slot."); return; }

        if (_cornerSeat.Count == 0) ResetCornerOccupancy();

        if (seat == _centeredSeat)
        {
            var already = FindSeatWindow(seat);
            if (already is not null) { try { _windowService.FocusWindow(already.Handle); } catch { } }
            Log.Info($"Seat {seat} ({SeatLabel(seat)}) is already centred.");
            return;
        }

        // Compute the full home-based target arrangement for "this seat centred". Determined purely by
        // the seat (not the current layout), so characters always return to their own corners.
        var target = BuildTargetOccupancy(seat);
        if (target is null)
        {
            // Home map lost sync (e.g. profile changed) — rebuild from a clean apply.
            Log.Warn($"Seat {seat} isn't in the current corner arrangement; re-applying layout.");
            ApplyActiveProfile();
            return;
        }

        var incoming = FindSeatWindow(seat);
        var outgoing = FindSeatWindow(_centeredSeat);
        var masterRect = ResolvePlacementRect(masterSlot);
        var parkRect = ResolveParkRect(masterRect);

        // Only two real windows ever move: the incoming client comes to the centre and the outgoing
        // client parks off-screen. Every other corner client is already parked — only its tile re-points.
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
            catch (Exception ex) { Log.Error($"Could not park seat {_centeredSeat}: {ex.Message}"); }
        }

        var outgoingSeat = _centeredSeat;
        _centeredSeat = seat;

        // Apply the new occupancy, repointing every corner tile + pill whose occupant changed.
        foreach (var (position, newSeat) in target)
        {
            if (_cornerSeat.GetValueOrDefault(position, int.MinValue) == newSeat) continue;
            _cornerSeat[position] = newSeat;
            var win = FindSeatWindow(newSeat);
            if (win is not null) UpdateCornerOverlay(position, win.Handle);
            else _cornerSourceHandles[position] = 0;
            RefreshPositionPill(position);
        }
        RefreshCenterPill();

        RefreshCornerOverlayZOrder();
        if (incoming is not null) { try { _windowService.FocusWindow(incoming.Handle); } catch { } }

        ScheduleAutoSave();
        Log.Info($"Centred seat {seat} ({SeatLabel(seat)}); seat {outgoingSeat} ({SeatLabel(outgoingSeat)}) returned to its home corner.");
    }

    // Flat-grid swap: no off-screen parking or thumbnails — every client occupies a visible tile. Bring
    // SEAT's client into the centre rect and move whichever clients changed positions to their new tiles.
    // Uses the same home-based occupancy bookkeeping as the overlay path, so a true 2-way master↔char
    // swap behaves identically in both modes and characters always return to their own corners.
    private void CenterSeatFlat(int seat)
    {
        var centerSlot = SelectedProfile!.Slots.FirstOrDefault(s => s.SlotNumber == CenterSlotNumber);
        if (centerSlot is null) { Log.Warn("Grid swap requires a layout with a centre slot."); return; }

        if (_cornerSeat.Count == 0) ResetCornerOccupancy();

        if (seat == _centeredSeat)
        {
            var already = FindSeatWindow(seat);
            if (already is not null) { try { _windowService.FocusWindow(already.Handle); } catch { } }
            return;
        }

        var target = BuildTargetOccupancy(seat);
        if (target is null)
        {
            Log.Warn($"Seat {seat} isn't in the current arrangement; re-applying layout.");
            ApplyActiveProfile();
            return;
        }

        // Desired occupant per position (centre included), and where each currently sits.
        int CurrentOccupant(int position) =>
            position == CenterSlotNumber ? _centeredSeat : _cornerSeat.GetValueOrDefault(position, position);

        foreach (var slot in SelectedProfile.Slots)
        {
            var desired = slot.SlotNumber == CenterSlotNumber ? seat : target.GetValueOrDefault(slot.SlotNumber, slot.SlotNumber);
            if (CurrentOccupant(slot.SlotNumber) == desired) continue;

            var window = FindSeatWindow(desired);
            if (window is null) continue;
            try { _windowService.MoveResizeWindow(window.Handle, ResolvePlacementRect(slot)); }
            catch (Exception ex) { Log.Error($"Could not move seat {desired} to position {slot.SlotNumber}: {ex.Message}"); }
        }

        var outgoingSeat = _centeredSeat;
        _centeredSeat = seat;
        _cornerSeat.Clear();
        foreach (var (position, newSeat) in target) _cornerSeat[position] = newSeat;

        var incoming = FindSeatWindow(seat);
        if (incoming is not null) { try { _windowService.FocusWindow(incoming.Handle); } catch { } }

        UpdatePositionCodes();
        ScheduleAutoSave();
        Log.Info($"Centred seat {seat} ({SeatLabel(seat)}); seat {outgoingSeat} ({SeatLabel(outgoingSeat)}) returned to its home corner.");
    }

    // Click-to-focus: bring the clicked corner tile's (or flyout's) occupant to the centre.
    // EULA-compliant focus switch — never forwards the click into the EVE client (COMPLIANCE.md).
    private void OnCornerTileClicked(int position)
    {
        if (!_settings.FocusPreviewOnClick) return;
        CloseFlyout();
        var seat = _cornerSeat.GetValueOrDefault(position, position);
        CenterSeat(seat);
    }

    // Hover-to-peek: start the debounce timer. The peek only triggers after the mouse has rested
    // on the tile for HoverPreviewDelayMs — cursors passing over tiles do nothing.
    private void OnCornerTileHovered(int position)
    {
        if (!_settings.HoverPreviewEnabled) return;

        _pendingHoverPosition = position;

        var delay = _settings.HoverPreviewDelayMs;
        if (delay <= 0)
        {
            // Instant mode — skip the timer.
            ExecuteHoverPeek(position);
            return;
        }

        _hoverPeekTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _hoverPeekTimer.Stop();
        _hoverPeekTimer.Start();
    }

    // Timer tick: the mouse has rested long enough — execute the peek.
    internal void OnHoverPeekTimerTick(object? sender, EventArgs e)
    {
        _hoverPeekTimer.Stop();
        if (_pendingHoverPosition < 0) return;
        ExecuteHoverPeek(_pendingHoverPosition);
    }

    private void ExecuteHoverPeek(int position)
    {
        if (SelectedProfile is null) return;

        var seat = _cornerSeat.GetValueOrDefault(position, position);
        if (seat == _centeredSeat) return;   // already centred — nothing to show

        var window = FindSeatWindow(seat);
        if (window is null) return;

        var centerSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == CenterSlotNumber);
        if (centerSlot is null) return;
        var masterRect = ResolvePlacementRect(centerSlot);

        if (!_cornerRects.TryGetValue(position, out var tileRect) || tileRect.Width == 0) return;

        var (fx, fy, fw, fh) = ComputeFlyoutRect(tileRect, masterRect);
        var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var dpiScale = monitor is null ? 1.0 : monitor.DpiX / 96.0;

        CloseFlyout();
        var flyout = new HoverFlyoutWindow(fx, fy, fw, fh, dpiScale, window.Handle);
        flyout.Clicked = () => OnCornerTileClicked(position);
        flyout.Show();
        _hoverFlyout = flyout;
    }

    private void OnCornerTileHoverLeft(int position)
    {
        _hoverPeekTimer.Stop();
        _pendingHoverPosition = -1;
        CloseFlyout();
    }

    private void CloseFlyout()
    {
        if (_hoverFlyout is null) return;
        try { _hoverFlyout.Close(); } catch { }
        _hoverFlyout = null;
    }

    // Flyout rect: ~50% of master width (capped at 720 px), maintaining master aspect ratio.
    // Anchored to the tile's inner corner (the edge facing the master centre).
    private static (int x, int y, int w, int h) ComputeFlyoutRect(WindowRect tile, WindowRect master)
    {
        var fw = Math.Min(master.Width / 2, 720);
        var fh = master.Width > 0 ? fw * master.Height / master.Width : fw * 9 / 16;

        var tileCx = tile.X + tile.Width / 2;
        var masterCx = master.X + master.Width / 2;
        var tileCy = tile.Y + tile.Height / 2;
        var masterCy = master.Y + master.Height / 2;

        var fx = tileCx < masterCx ? tile.X + tile.Width : tile.X - fw;
        var fy = tileCy < masterCy ? tile.Y + tile.Height : tile.Y - fh;

        return (fx, fy, fw, fh);
    }

    // ── Per-tile updates ───────────────────────────────────────────────────────

    // Repoint a corner tile at a new source HWND (called after a centre swap).
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
            pill.SetContent(PillTextForPosition(position),
                SeatPortraitUrl(_cornerSeat.GetValueOrDefault(position, position)));
    }

    private void RefreshCenterPill()
    {
        if (_pills.TryGetValue(CenterSlotNumber, out var pill))
            pill.SetContent(CenterPillText(), SeatPortraitUrl(_centeredSeat));
    }

    // Refresh every pill's caption (used when a label-display setting changes).
    internal void RefreshAllPills()
    {
        foreach (var position in _cornerSeat.Keys) RefreshPositionPill(position);
        RefreshCenterPill();
    }

    internal void RefreshCornerOverlayZOrder()
    {
        foreach (var overlay in _cornerOverlays.Values)
            overlay.RefreshZOrder();
    }

    // ── Per-tick upkeep ────────────────────────────────────────────────────────

    // Re-assert Z-order, refresh pills, and re-register any tile whose seat client was
    // closed/restarted (handle gone or changed — e.g. a relog gives a new HWND).
    internal void MaintainCornerOverlays()
    {
        if (_cornerOverlays.Count == 0) return;

        // Pills only float on top when EVE or EWC is in the foreground; hide behind everything else.
        var fg = _windowService.GetForegroundWindowHandle();
        var eveOrEwcFg = fg != 0 && (Windows.Any(w => w.Handle == fg) ||
            fg == new System.Windows.Interop.WindowInteropHelper(System.Windows.Application.Current.MainWindow).Handle);
        foreach (var pill in _pills.Values)
            pill.SetTopmost(eveOrEwcFg);

        // Cancel any pending or active flyout when EVE/EWC loses focus.
        if (!eveOrEwcFg && (_hoverFlyout is not null || _pendingHoverPosition >= 0 || _cursorOverPosition >= 0))
        {
            if (_cursorOverPosition >= 0) OnCornerTileHoverLeft(_cursorOverPosition);
            _cursorOverPosition = -1;
        }

        // Tiles sit at HWND_BOTTOM and never receive WM_MOUSEMOVE, so poll the cursor directly.
        // Only trigger flyouts when EVE/EWC is in the foreground — avoids peek appearing while typing elsewhere.
        if (_settings.HoverPreviewEnabled && eveOrEwcFg && Utilities.Win32Native.GetCursorPos(out var cur))
        {
            int hitPos = -1;
            foreach (var (pos, r) in _cornerRects)
            {
                if (cur.X >= r.X && cur.X < r.X + r.Width && cur.Y >= r.Y && cur.Y < r.Y + r.Height)
                { hitPos = pos; break; }
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
            var seat = _cornerSeat.GetValueOrDefault(position, position);
            var window = FindSeatWindow(seat);
            if (window is null)
            {
                // Seat's client closed — hide the stale tile + blank its pill.
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

            overlay.RefreshZOrder();
        }

        // Centre pill: hide when the centred seat's client is gone, else keep its name current.
        if (_pills.TryGetValue(CenterSlotNumber, out var masterPill))
        {
            var centeredWindow = FindSeatWindow(_centeredSeat);
            masterPill.SetContent(centeredWindow is null ? "" : CenterPillText(), SeatPortraitUrl(_centeredSeat));
        }
    }
}
