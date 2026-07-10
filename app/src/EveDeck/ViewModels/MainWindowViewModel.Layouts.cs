using System.IO;
using System.Windows;
using WpfApp = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using EveDeck.Models;
using EveDeck.Services;
using Microsoft.Win32;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ── Profile application ────────────────────────────────────────────────────

    // 1c — Window moves run off the UI thread to prevent brief freezes with many clients.
    private async void ApplyActiveProfile()
    {
        if (_applyInProgress) return;
        if (SelectedProfile is null) { Log.Warn("No active profile selected."); return; }

        if (!UsePhysicalPixels)
            Log.Warn("Applying layout with scaled logical coordinates enabled; Windows scaling can alter final physical placement.");

        // Corner overlay mode: all clients go to master resolution; non-master windows park off-screen.
        // Only profiles that actually form a grid use it; single/stacked layouts fall through to plain
        // window placement ("copy") even with corner overlays globally enabled.
        if (_settings.CornerOverlaysEnabled && SelectedProfile.SupportsCornerGrid)
        {
            await ApplyCornerOverlayLayout();
            return;
        }

        WarnIfAssignedWindowsExceedUniquePositions(SelectedProfile);
        WarnIfProfileExceedsTarget(SelectedProfile);

        // 3b — Snapshot current window positions before moving anything (enables undo).
        _undoRects = Windows.GroupBy(w => w.Title).ToDictionary(g => g.Key, g => g.First().Rect.Clone());

        // Resolve which POSITION slot each seat occupies at rest. For grid profiles this honours the
        // master (→ centre) and the per-seat home corners, so flat-tiled mode lays out the same way the
        // corner-overlay mode does — and the master↔character swap is coherent in both. Non-grid layouts
        // keep the simple identity mapping (seat number == slot number).
        EnsureValidMasterSeat();
        var seatSlots = ResolveSeatPositionSlots();

        // Pre-resolve everything on the UI thread before going async.
        var workItems = (
            from assignment in Assignments
            where assignment.AssignedWindows.Count > 0
            let slot = seatSlots.GetValueOrDefault(assignment.SlotNumber)
            where slot is not null
            let targetRect = slot!.SlotNumber == CenterSlotNumber
                ? ApplyMasterResOverride(ResolvePlacementRect(slot))
                : ResolvePlacementRect(slot)
            from window in FindAssignedWindows(assignment)
            select (window, slot, targetRect, slot.Borderless)
        ).ToList();

        foreach (var (_, slot, _, _) in workItems.Where(x => x.Borderless))
        {
            var w = workItems.First(x => x.slot == slot).window;
            SaveStyleSnapshotIfMissing(w);
        }

        if (workItems.Count == 0)
        {
            Log.Warn("No assigned windows matched the active profile slots.");
            return;
        }

        _applyInProgress = true;
        UndoLastApplyCommand.RaiseCanExecuteChanged();

        await System.Threading.Tasks.Task.Run(async () =>
        {
            foreach (var (window, slot, targetRect, borderless) in workItems)
            {
                try
                {
                    // Move first so WM_NCCALCSIZE (from MakeBorderless) fires at the correct position.
                    _windowService.MoveResizeWindow(window.Handle, targetRect);
                    if (borderless) _windowService.MakeBorderless(window.Handle);
                    WpfApp.Current.Dispatcher.Invoke(() =>
                        Log.Info($"Moved {window.Title} to slot {slot.SlotNumber}: {targetRect}."));
                }
                catch (Exception ex)
                {
                    WpfApp.Current.Dispatcher.Invoke(() =>
                        Log.Error($"Could not apply slot {slot.SlotNumber} to {window.Title}: {ex.Message}"));
                }
            }

            // Re-apply positions after a short delay to override any deferred self-correction
            // the game may post to its own message queue after processing WM_NCCALCSIZE.
            await System.Threading.Tasks.Task.Delay(300);
            foreach (var (window, _, targetRect, _) in workItems)
            {
                try { _windowService.MoveResizeWindow(window.Handle, targetRect); }
                catch { } // best-effort second pass; window may have closed mid-apply
            }
        });

        _applyInProgress = false;
        UndoLastApplyCommand.RaiseCanExecuteChanged();
        Refresh();

        // Re-baseline swap bookkeeping so a flat-grid layout starts at rest (master centred).
        if (SelectedProfile.SupportsCornerGrid)
        {
            ResetCornerOccupancy();
            UpdatePositionCodes();
        }

        ApplyTopmostState();
    }

    // Maps each seat (SlotAssignment.SlotNumber) to the profile POSITION slot it occupies at rest. Grid
    // profiles place the master in the centre and every other seat in its home corner; non-grid layouts
    // use the identity mapping (seat number == slot number).
    private Dictionary<int, LayoutSlot> ResolveSeatPositionSlots()
    {
        var map = new Dictionary<int, LayoutSlot>();
        if (SelectedProfile is null) return map;

        if (!SelectedProfile.SupportsCornerGrid)
        {
            foreach (var a in Assignments)
            {
                var s = SelectedProfile.Slots.FirstOrDefault(x => x.SlotNumber == a.SlotNumber);
                if (s is not null) map[a.SlotNumber] = s;
            }
            return map;
        }

        var centerSlot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == CenterSlotNumber);
        if (centerSlot is not null) map[ActiveMasterSeat] = centerSlot;
        foreach (var (position, seat) in ComputeHomeArrangement())
        {
            var s = SelectedProfile.Slots.FirstOrDefault(x => x.SlotNumber == position);
            if (s is not null) map[seat] = s;
        }
        return map;
    }

    // The CENTRE slot is pure geometry: the large rect the four corners surround (= the biggest slot).
    // It's derived from the layout, so it can never "drift" the way a stored slot number can.
    private int CenterSlotNumber
    {
        get
        {
            // NB: settings fallback (not ActiveMasterSeat) — ActiveMasterSeat calls back here, so using
            // it would recurse when a profile exists but has no slots.
            if (SelectedProfile is null || SelectedProfile.Slots.Count == 0) return _settings.MasterSlotNumber;
            return SelectedProfile.Slots
                .OrderByDescending(s => (long)s.Width * s.Height)
                .First().SlotNumber;
        }
    }

    // The MASTER is a SEAT (character/account), not a position: whichever seat the user designated as
    // master is centred at rest and is the F-key "home". Independent of geometry, so setting a corner
    // seat (e.g. a corner seat in slot 1) as master simply centres that account — it never demotes
    // the real centre rect into an overlapping thumbnail.
    //
    // Two distinct cases:
    //  * The designated master seat was DELETED (slot removed): permanently fall back to the centre
    //    slot and PERSIST it — the old seat is gone for good.
    //  * The designated master seat exists but its client isn't running (partial session): TRANSIENTLY
    //    promote the highest-priority (lowest-numbered) seat that IS running via _promotedMasterSeat,
    //    filling the master GEOMETRY slot without overwriting the user's saved master. ComputeHome-
    //    Arrangement then lays the remaining seats around it, so this stays correct for non-square
    //    families (Side Stack/Whammy, where the master is the HIGHEST-numbered big slot). The promotion
    //    is re-evaluated from scratch on every apply/profile-switch and snaps back to the real master
    //    the moment its client is detected again — it is NEVER saved, which is what used to scramble
    //    these layouts permanently.
    //
    // "Running" MUST mean a live, currently-detected EVE window (FindAssignedWindows), not just
    // AssignedWindows.Count > 0 — that list is persisted config that survives a client closing.
    private void EnsureValidMasterSeat()
    {
        if (SelectedProfile is null || SelectedProfile.Slots.Count == 0) return;

        // Re-evaluate from scratch: drop any prior transient promotion so the persisted master is the
        // baseline again (and so a master that came back online resumes control).
        _promotedMasterSeat = null;

        var designated = ActiveMasterSeat;
        var master = Assignments.FirstOrDefault(a => a.SlotNumber == designated);

        // Deleted master seat -> permanent, persisted fallback to the geometric centre.
        if (master is null)
        {
            var fallback = CenterSlotNumber;
            if (fallback == designated) return;
            Log.Warn($"Master seat {designated} no longer exists; defaulting to seat {fallback}.");
            ActiveMasterSeat = fallback;
            SyncMasterSlot();
            UpdatePositionCodes();
            OnPropertyChanged(nameof(MasterSlotNumber));
            return;
        }

        // Designated master's client is running -> use it as-is.
        if (FindAssignedWindows(master).Any()) return;

        // Partial session: transiently centre the highest-priority running seat. No change if the
        // designated master is the only one that would qualify or nobody else is running.
        var promoted = Assignments
            .Where(a => a.SlotNumber != designated)
            .OrderBy(a => a.SlotNumber)
            .FirstOrDefault(a => FindAssignedWindows(a).Any());
        if (promoted is null) return;

        _promotedMasterSeat = promoted.SlotNumber;
        SyncMasterSlot();
        UpdatePositionCodes();
        OnPropertyChanged(nameof(MasterSlotNumber));
        RaiseIdentityDependents();
        Log.Info($"Master seat {designated} ({master.Label}) is not logged in; temporarily centring seat {promoted.SlotNumber} ({promoted.Label}) for this session (not saved).");
    }

    // Corner overlay apply: resize ALL clients to master resolution, park non-master off-screen left,
    // place the master SEAT in the centre rect, then start corner thumbnail overlays.
    private async System.Threading.Tasks.Task ApplyCornerOverlayLayout()
    {
        EnsureValidMasterSeat();

        // Centre geometry comes from the largest slot; the centred CHARACTER is the master seat.
        var centerSlot = SelectedProfile!.Slots.FirstOrDefault(s => s.SlotNumber == CenterSlotNumber);
        if (centerSlot is null)
        {
            Log.Warn("Preview mode requires a layout with a centre slot.");
            return;
        }

        var masterRect = ResolveMasterRect(centerSlot);
        var masterAssignment = Assignments.FirstOrDefault(a => a.SlotNumber == ActiveMasterSeat);
        var masterWindow = masterAssignment is null ? null : FindAssignedWindows(masterAssignment).FirstOrDefault();

        // Collect non-master assigned windows now; the master is moved separately below so we can
        // requery its ACTUAL post-move size before parking anyone off it (see note below).
        var nonMasterMoves = new List<(EveWindowInfo window, bool borderless)>();
        foreach (var assignment in Assignments.Where(a => a.AssignedWindows.Count > 0 && a.SlotNumber != ActiveMasterSeat))
        {
            var slot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == assignment.SlotNumber);
            var borderless = slot?.Borderless ?? true;
            foreach (var window in FindAssignedWindows(assignment))
            {
                if (borderless) SaveStyleSnapshotIfMissing(window);
                nonMasterMoves.Add((window, borderless));
            }
        }

        if (masterWindow is null && nonMasterMoves.Count == 0) { Log.Warn("No assigned windows found for preview mode layout."); return; }

        _applyInProgress = true;
        UndoLastApplyCommand.RaiseCanExecuteChanged();
        _undoRects = Windows.GroupBy(w => w.Title).ToDictionary(g => g.Key, g => g.First().Rect.Clone());

        var masterSlotBorderless = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == ActiveMasterSeat)?.Borderless ?? true;

        await System.Threading.Tasks.Task.Run(async () =>
        {
            if (masterWindow is not null)
            {
                try
                {
                    if (masterSlotBorderless) SaveStyleSnapshotIfMissing(masterWindow);
                    _windowService.MoveResizeWindow(masterWindow.Handle, masterRect);
                    if (masterSlotBorderless) _windowService.MakeBorderless(masterWindow.Handle);
                }
                catch (Exception ex)
                {
                    WpfApp.Current.Dispatcher.Invoke(() =>
                        Log.Error($"Preview apply failed for master {masterWindow.Title}: {ex.Message}"));
                }

                await System.Threading.Tasks.Task.Delay(300);
                try { _windowService.MoveResizeWindow(masterWindow.Handle, masterRect); } catch { }
            }
        });

        // Some EVE clients enforce their own fixed window/render resolution (e.g. "Window mode" set
        // in the in-game Settings dialog) and silently ignore or ratio-clamp an external resize. Park
        // geometry must follow whatever the master ACTUALLY ended up at, not the size we asked for —
        // otherwise every parked alt gets force-resized to a mismatched size, reflowing their UI.
        var actualMasterRect = masterWindow is not null && _windowService.TryGetWindowRect(masterWindow.Handle, out var liveRect)
            ? liveRect
            : masterRect;
        if (actualMasterRect.Width != masterRect.Width || actualMasterRect.Height != masterRect.Height)
            Log.Warn($"Master window did not accept the requested size ({masterRect.Width}x{masterRect.Height}); it is actually {actualMasterRect.Width}x{actualMasterRect.Height} (likely a fixed in-game window/resolution setting). Parking alts at the actual size instead.");
        var parkRect = ResolveParkRect(actualMasterRect);

        var moves = nonMasterMoves.Select(m => (m.window, rect: parkRect, m.borderless)).ToList();

        await System.Threading.Tasks.Task.Run(async () =>
        {
            foreach (var (window, rect, borderless) in moves)
            {
                try
                {
                    _windowService.MoveResizeWindow(window.Handle, rect);
                    if (borderless) _windowService.MakeBorderless(window.Handle);
                    WpfApp.Current.Dispatcher.Invoke(() =>
                        Log.Info($"{window.Title} → park {rect}."));
                }
                catch (Exception ex)
                {
                    WpfApp.Current.Dispatcher.Invoke(() =>
                        Log.Error($"Preview apply failed for {window.Title}: {ex.Message}"));
                }
            }

            await System.Threading.Tasks.Task.Delay(300);
            foreach (var (window, rect, _) in moves)
            {
                try { _windowService.MoveResizeWindow(window.Handle, rect); } catch { } // best-effort second pass; window may have closed mid-apply
            }
        });

        _applyInProgress = false;
        UndoLastApplyCommand.RaiseCanExecuteChanged();
        Refresh();

        // Baseline occupancy: centre = master seat, each corner shows its own seat.
        ResetCornerOccupancy();

        // Start corner overlays on the UI thread after windows are in place.
        StartCornerOverlays();

        // Bring the master client to the foreground so it floats on top of the corners.
        if (masterWindow is not null)
        {
            try { _windowService.FocusWindow(masterWindow.Handle); } catch { } // best-effort focus; window may have closed mid-apply
        }

        ApplyTopmostState();
        Log.Info("Preview mode layout applied.");
    }

    // Park position for non-master clients: fully off-screen to the left of the target
    // monitor so no sliver pokes back onto the display, while DWM keeps compositing them
    // (off-screen windows still render; only minimized windows stop). Parked clients stay at
    // master resolution — never resized — so EVE never re-flows its UI layout.
    private WindowRect ResolveParkRect(WindowRect masterRect)
    {
        var anchor = ResolveLayoutAnchor();
        var leftEdge = anchor?.X ?? 0;
        return new WindowRect
        {
            X = leftEdge - masterRect.Width - 100,
            Y = masterRect.Y,
            Width = masterRect.Width,
            Height = masterRect.Height
        };
    }

    // 3b — Restore window positions from the snapshot captured before the last apply.
    private void UndoLastApply()
    {
        if (_undoRects is null) return;
        foreach (var (title, rect) in _undoRects)
        {
            var window = FindWindowByTitle(title);
            if (window is null) continue;
            try
            {
                _windowService.MoveResizeWindow(window.Handle, rect);
                Log.Info($"Undo: restored {title} to {rect}.");
            }
            catch (Exception ex)
            {
                Log.Error($"Undo failed for {title}: {ex.Message}");
            }
        }
        _undoRects = null;
        UndoLastApplyCommand.RaiseCanExecuteChanged();
        Refresh();
    }

    private void CaptureAssignedWindows()
    {
        if (SelectedProfile is null) return;
        if (SelectedProfile.IsBuiltIn)
        {
            var capturedProfile = SelectedProfile.Clone($"Captured - {SelectedProfile.Name}");
            Profiles.Add(capturedProfile);
            SelectedProfile = capturedProfile;
            Log.Info("Built-in presets are protected. Created a captured custom copy instead.");
        }

        foreach (var assignment in Assignments)
        {
            var window = FindAssignedWindows(assignment).FirstOrDefault();
            if (window is null) continue;

            var slot = SelectedProfile.Slots.FirstOrDefault(s => s.SlotNumber == assignment.SlotNumber);
            if (slot is null)
            {
                slot = new LayoutSlot { SlotNumber = assignment.SlotNumber };
                SelectedProfile.Slots.Add(slot);
            }

            slot.Label = assignment.Label;
            var capturedRect = ResolveCapturedRect(window);
            slot.X = capturedRect.X;
            slot.Y = capturedRect.Y;
            slot.Width = capturedRect.Width;
            slot.Height = capturedRect.Height;
            slot.MonitorId = window.MonitorId;
            slot.Borderless = window.IsBorderless;
            Log.Info($"Captured slot {slot.SlotNumber} from {window.Title}: {capturedRect}.");
        }

        var captureMon = string.IsNullOrWhiteSpace(LayoutTargetMonitorId) ? null
            : Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId);
        if (captureMon is not null)
        {
            SelectedProfile.CaptureMonitorX = captureMon.Bounds.X;
            SelectedProfile.CaptureMonitorY = captureMon.Bounds.Y;
            SelectedProfile.CaptureMonitorWidth = captureMon.Bounds.Width;
            SelectedProfile.CaptureMonitorHeight = captureMon.Bounds.Height;
        }

        Save();
        OnPropertyChanged(nameof(ActiveProfileSlots));
        RebuildLayoutPreview();
    }

    // ── Profile CRUD ───────────────────────────────────────────────────────────

    // New custom profile: ask for the account count, seed a grid of that many slots, then open the
    // on-monitor editor so the user places them visually right away.
    private void NewProfile()
    {
        var defaultCount = Windows.Count > 0 ? Math.Clamp(Windows.Count, 1, 15) : 4;
        var dialog = new Views.NewProfileDialog(defaultCount)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;

        var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var profile = PresetFactory.CreateCustomProfile(
            UniqueProfileName("Custom Layout"),
            monitor?.Bounds.Width ?? 2560,
            monitor?.Bounds.Height ?? 1440,
            dialog.AccountCount);
        Profiles.Add(profile);
        SelectedProfile = profile;
        Save();
        EditLayoutOnMonitor();
    }

    private void DuplicateProfile()
    {
        if (SelectedProfile is null) return;
        var clone = SelectedProfile.Clone();
        Profiles.Add(clone);
        SelectedProfile = clone;
        Save();
    }

    private void DeleteProfile()
    {
        if (SelectedProfile is null || Profiles.Count <= 1) return;
        if (SelectedProfile.IsBuiltIn) { Log.Warn("Built-in presets cannot be deleted. Duplicate one to make a custom profile."); return; }

        var result = MessageBox.Show(
            $"Delete profile '{SelectedProfile.Name}'?",
            "Delete Profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var removed = SelectedProfile;
        Profiles.Remove(removed);
        SelectedProfile = Profiles.FirstOrDefault();
        Save();
        Log.Info($"Deleted profile {removed.Name}.");
    }

    private void ImportProfile()
    {
        var dialog = new OpenFileDialog { Filter = "Layout profile (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var profile = _configService.ImportProfile(dialog.FileName);
            Profiles.Add(profile);
            SelectedProfile = profile;
            Save();
            Log.Info($"Imported profile from {dialog.FileName}.");
        }
        catch (Exception ex) { Log.Error($"Import failed: {ex.Message}"); }
    }

    private void ExportProfile()
    {
        if (SelectedProfile is null) return;
        var safeName = string.Join("_", SelectedProfile.Name.Split(Path.GetInvalidFileNameChars()));
        var dialog = new SaveFileDialog { Filter = "Layout profile (*.json)|*.json", FileName = $"{safeName}.json" };
        if (dialog.ShowDialog() != true) return;
        try { _configService.ExportProfile(SelectedProfile, dialog.FileName); Log.Info($"Exported profile to {dialog.FileName}."); }
        catch (Exception ex) { Log.Error($"Export failed: {ex.Message}"); }
    }

    // ── On-monitor layout editor ───────────────────────────────────────────────

    // Open the full-screen WYSIWYG slot editor on the target monitor. Slots are shown where layout
    // apply would actually place them (via ResolvePlacementRect), edited in physical pixels, and
    // saved back as a monitor-absolute custom profile. Editing a built-in silently edits a
    // "<Name> (Custom)" copy so presets stay pristine.
    private void EditLayoutOnMonitor()
    {
        if (SelectedProfile is null || SelectedProfile.Slots.Count == 0)
        {
            Log.Warn("Select a profile with at least one slot to edit.");
            return;
        }

        var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        if (monitor is null)
        {
            Log.Warn("No monitor detected - cannot open the layout editor.");
            return;
        }

        var items = SelectedProfile.Slots
            .OrderBy(s => s.SlotNumber)
            .Select(s =>
            {
                var r = ResolvePlacementRect(s);
                return new Views.LayoutEditorSlot
                {
                    SlotNumber = s.SlotNumber,
                    Label = s.Label,
                    X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
                };
            })
            .ToList();

        var editor = new Views.LayoutEditorWindow(Monitors.ToList(), monitor, items);
        if (editor.ShowDialog() != true) return;
        ApplyEditedSlots(monitor, editor.ResultSlots);
    }

    private void ApplyEditedSlots(MonitorInfo monitor, IReadOnlyList<Views.LayoutEditorSlot> edited)
    {
        if (SelectedProfile is null || edited.Count == 0) return;

        var source = SelectedProfile;
        var target = source;
        if (source.IsBuiltIn)
        {
            target = source.Clone(UniqueProfileName($"{source.Name} (Custom)"));
            Profiles.Add(target);
        }

        // Carry per-slot Borderless over from the pre-edit slots where numbers still line up.
        var borderlessByNumber = source.Slots.ToDictionary(s => s.SlotNumber, s => s.Borderless);

        target.Slots.Clear();
        foreach (var s in edited.OrderBy(x => x.SlotNumber))
        {
            // Tag each slot with the monitor its centre landed on (editor spans all monitors).
            var cx = s.X + s.Width / 2;
            var cy = s.Y + s.Height / 2;
            var slotMonitor = Monitors.FirstOrDefault(m =>
                    cx >= m.Bounds.X && cx < m.Bounds.X + m.Bounds.Width
                    && cy >= m.Bounds.Y && cy < m.Bounds.Y + m.Bounds.Height)
                ?? monitor;

            target.Slots.Add(new LayoutSlot
            {
                SlotNumber = s.SlotNumber,
                Label = string.IsNullOrWhiteSpace(s.Label) ? $"Slot {s.SlotNumber}" : s.Label,
                MonitorId = slotMonitor.Id,
                X = s.X, Y = s.Y, Width = s.Width, Height = s.Height,
                Borderless = borderlessByNumber.GetValueOrDefault(s.SlotNumber, true),
            });
        }

        // The saved coordinates are literal physical rects on THIS monitor: store its bounds so
        // ResolvePlacementRect path 1 reproduces (and later rescales) exactly what was drawn, and
        // disable AvoidTaskbar so the work-area anchor doesn't shift the drawn rects a second time.
        target.CaptureMonitorX = monitor.Bounds.X;
        target.CaptureMonitorY = monitor.Bounds.Y;
        target.CaptureMonitorWidth = monitor.Bounds.Width;
        target.CaptureMonitorHeight = monitor.Bounds.Height;
        target.AvoidTaskbar = false;
        target.IsFamilyTemplate = false;

        // Slots may have been removed/renumbered: drop dangling swap-group members and master seat.
        var count = target.Slots.Count;
        for (var i = target.SwapGroups.Count - 1; i >= 0; i--)
        {
            target.SwapGroups[i].SlotNumbers.RemoveAll(n => n > count);
            if (target.SwapGroups[i].SlotNumbers.Count < 2) target.SwapGroups.RemoveAt(i);
        }
        if (target.MasterSeat > count) target.MasterSeat = 0;

        if (!ReferenceEquals(SelectedProfile, target))
        {
            SelectedProfile = target; // raises all profile-dependent state itself
        }
        else
        {
            OnPropertyChanged(nameof(ActiveProfileSlots));
            UpdatePositionCodes();
            RebuildLayoutPreview();
        }
        Save();
        Log.Info($"Saved edited layout '{target.Name}' ({count} slots on {monitor.Id}).");
    }

    private string UniqueProfileName(string baseName)
    {
        var name = baseName;
        var i = 2;
        while (Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} {i++}";
        return name;
    }

    // ── Move / Swap ────────────────────────────────────────────────────────────

    private void MoveActiveToSlot(int slotNumber)
    {
        Refresh();
        var active = FindActiveManagedWindow();
        var targetAssignment = Assignments.FirstOrDefault(a => a.SlotNumber == slotNumber);
        var targetSlot = SelectedProfile?.Slots.FirstOrDefault(s => s.SlotNumber == slotNumber);
        if (active is null || targetAssignment is null || targetSlot is null)
        {
            Log.Warn($"Could not move active window to slot {slotNumber}.");
            return;
        }

        foreach (var other in Assignments)
        {
            var dup = other.AssignedWindows.FirstOrDefault(e => e.Title.Equals(active.Title, StringComparison.OrdinalIgnoreCase));
            if (dup is not null) other.AssignedWindows.Remove(dup);
        }

        targetAssignment.AssignedWindows.Add(new SlotWindowEntry { Title = active.Title, LastProcessId = active.ProcessId, LastHandleHex = active.HandleHex });
        _windowService.MoveResizeWindow(active.Handle, ResolvePlacementRect(targetSlot));
        Save();
        Log.Info($"Moved active window {active.Title} to slot {slotNumber}.");
    }

    private void SwapActiveWithSlot(int slotNumber)
    {
        Refresh();
        var active = FindActiveManagedWindow();
        var targetAssignment = Assignments.FirstOrDefault(a => a.SlotNumber == slotNumber);
        var activeAssignment = active is null ? null : Assignments.FirstOrDefault(a =>
            a.AssignedWindows.Any(e => e.Title.Equals(active.Title, StringComparison.OrdinalIgnoreCase)));
        if (active is null || targetAssignment is null || activeAssignment is null)
        {
            Log.Warn($"Could not swap active window with slot {slotNumber}.");
            return;
        }

        var activeWindows = activeAssignment.AssignedWindows.ToList();
        var targetWindows = targetAssignment.AssignedWindows.ToList();
        activeAssignment.AssignedWindows.Clear();
        targetAssignment.AssignedWindows.Clear();
        foreach (var w in targetWindows) activeAssignment.AssignedWindows.Add(w);
        foreach (var w in activeWindows) targetAssignment.AssignedWindows.Add(w);
        Save();
        ApplyActiveProfile();
        Log.Info($"Swapped slot {activeAssignment.SlotNumber} with slot {slotNumber}.");
    }

    // Bring the foreground EVE window's seat to the centre.
    private void SwapFocusedWithMaster()
    {
        Refresh();
        var active = FindActiveManagedWindow();
        if (active is null) { Log.Warn("No active EVE window found to centre."); return; }

        var seat = Assignments.FirstOrDefault(a =>
            a.AssignedWindows.Any(e => e.Title.Equals(active.Title, StringComparison.OrdinalIgnoreCase)));
        if (seat is null) { Log.Warn("Active window is not assigned to any seat."); return; }

        CenterSeat(seat.SlotNumber);
    }

    // Seat-targeting switch: bring an account's CLIENT to the centre, identified by the seat's
    // main character (Label). Model A — seats/accounts are fixed, so this works even when a
    // non-main character is logged into that account (we centre whatever client the seat holds).
    private void SwitchToCharacter(string? mainCharacter)
    {
        if (string.IsNullOrWhiteSpace(mainCharacter))
        {
            Log.Warn("This switch hotkey has no seat set. Pick a character in the Hotkeys tab → Character column.");
            return;
        }

        Refresh();
        mainCharacter = mainCharacter.Trim();

        // Resolve the SEAT by its main-character label (stable, fixed per slot). Fall back to the
        // currently-logged-in window title in case the user typed a specific character name.
        var seat = Assignments.FirstOrDefault(a =>
                a.Label.Equals(mainCharacter, StringComparison.OrdinalIgnoreCase))
            ?? Assignments.FirstOrDefault(a =>
                a.AssignedWindows.Any(e => TitleMatchesCharacter(e.Title, mainCharacter)));

        if (seat is null)
        {
            Log.Warn($"No seat found for '{mainCharacter}'.");
            return;
        }

        CenterSeat(seat.SlotNumber);
    }

    // True when an EVE window title belongs to the named character. Matches the stripped character
    // name exactly, or tolerates the user having typed a partial/full title.
    private static bool TitleMatchesCharacter(string title, string characterName)
    {
        var character = CharacterNameFromTitle(title);
        return character.Equals(characterName, StringComparison.OrdinalIgnoreCase)
            || character.Contains(characterName, StringComparison.OrdinalIgnoreCase)
            || title.Contains(characterName, StringComparison.OrdinalIgnoreCase);
    }

    // Hotkey entry point (SwapSlotWithMasterN / Ctrl+Shift+N) — centre the seat. Kept under the old
    // name for hotkey routing + SafetyGuard compatibility.
    private void SwapSlotWithMaster(int slotNumber) => CenterSeat(slotNumber);

    // Whether the profile's centre slot is meaningfully larger (>= 1.5x area) than every other slot
    // in the group -- true for Center Master/Whammy/Side Stack/Twin Stack, which are built with one
    // deliberately oversized master cell. Grid-family and custom equal-cell layouts have no such
    // cell -- every slot is roughly the same size, so the "centre" is just one arbitrary cell among
    // equals.
    private bool HasDominantMasterSlot(LayoutSlot centerSlot)
    {
        var centerArea = (long)centerSlot.Width * centerSlot.Height;
        return SelectedProfile is null || SelectedProfile.Slots
            .Where(s => s.SlotNumber != centerSlot.SlotNumber)
            .All(s => centerArea >= (long)s.Width * s.Height * 3 / 2);
    }

    // Resolve the real, physical size/position the master's EVE window should be moved to. For
    // layouts with a dominant master cell this is just that cell (scaled/overridden as before). For
    // equal-cell layouts (Grid family) resizing the real window down to one small grid cell would
    // shrink EVE's actual render resolution to a fraction of the screen -- the "everyone gets
    // shrunk tiny" bug reported against Grid. Instead the master keeps a full/native resolution
    // (still subject to any per-profile Master Resolution override) and gets its own preview tile
    // in the grid just like every other seat -- see StartCornerOverlays.
    private WindowRect ResolveMasterRect(LayoutSlot centerSlot)
    {
        if (HasDominantMasterSlot(centerSlot)) return ApplyMasterResOverride(ResolvePlacementRect(centerSlot));

        var full = ResolveLayoutAnchor() ?? ResolvePlacementRect(centerSlot);
        return ApplyMasterResOverride(full);
    }

    // If the active profile has a master resolution override, replace the width/height of the given
    // rect with it (keeping X/Y), clamped to the current monitor bounds so the window never
    // overflows the desktop. Returns the rect unchanged when no override is set.
    private WindowRect ApplyMasterResOverride(WindowRect r)
    {
        if (SelectedProfile?.MasterResolutionWidth is not > 0) return r;
        var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var w = monitor is null ? SelectedProfile.MasterResolutionWidth
            : Math.Min(SelectedProfile.MasterResolutionWidth, monitor.Bounds.Width);
        var h = monitor is null ? SelectedProfile.MasterResolutionHeight
            : Math.Min(SelectedProfile.MasterResolutionHeight, monitor.Bounds.Height);
        if (monitor is not null && (w != SelectedProfile.MasterResolutionWidth || h != SelectedProfile.MasterResolutionHeight))
            Log.Warn($"Master resolution override {SelectedProfile.MasterResolutionWidth}x{SelectedProfile.MasterResolutionHeight} exceeds desktop bounds {monitor.Bounds.Width}x{monitor.Bounds.Height} — clamped to {w}x{h}. Enable VSR/DSR and switch your desktop resolution first.");
        return new WindowRect { X = r.X, Y = r.Y, Width = w, Height = h };
    }

    // ── Rect resolution ────────────────────────────────────────────────────────

    // Map a slot to its on-screen rect for the current target monitor. Every path here is
    // resolution-independent: the profile is scaled to whatever the target display reports right now,
    // so the same profile lands correctly on the native panel, a VSR/DSR virtual resolution, or any
    // Windows display-scale — the desktop compositor only ever sees physical-pixel rects.
    private WindowRect ResolvePlacementRect(LayoutSlot slot)
    {
        var anchor = ResolveLayoutAnchor();

        // 1. Custom profile captured on a known monitor: scale proportionally from the capture
        //    resolution to the target. This is the most faithful mapping (preserves any intentional
        //    gaps/margins the user captured) and survives any resolution / VSR / scale change.
        if (anchor is not null && UsePhysicalPixels
            && SelectedProfile?.IsBuiltIn == false
            && SelectedProfile.CaptureMonitorWidth > 0
            && SelectedProfile.CaptureMonitorHeight > 0)
        {
            double scaleX = (double)anchor.Width  / SelectedProfile.CaptureMonitorWidth;
            double scaleY = (double)anchor.Height / SelectedProfile.CaptureMonitorHeight;
            return new WindowRect
            {
                X      = anchor.X + (int)Math.Round((slot.X - SelectedProfile.CaptureMonitorX) * scaleX),
                Y      = anchor.Y + (int)Math.Round((slot.Y - SelectedProfile.CaptureMonitorY) * scaleY),
                Width  = (int)Math.Round(slot.Width  * scaleX),
                Height = (int)Math.Round(slot.Height * scaleY),
            };
        }

        // 2. Any other profile with a target monitor (built-in presets, or custom profiles that have
        //    no stored capture resolution): scale the profile's own bounding box to fill the anchor
        //    (monitor bounds or work area). This is what makes presets fit any display and "Avoid
        //    taskbar" actually shrink/shift slots into the work area.
        if (anchor is not null && SelectedProfile is not null && SelectedProfile.Slots.Count > 0)
        {
            var slots = SelectedProfile.Slots;
            var profileMinX = slots.Min(s => s.X);
            var profileMinY = slots.Min(s => s.Y);
            var profileW = slots.Max(s => s.X + s.Width) - profileMinX;
            var profileH = slots.Max(s => s.Y + s.Height) - profileMinY;

            double scaleX = profileW > 0 ? (double)anchor.Width / profileW : 1.0;
            double scaleY = profileH > 0 ? (double)anchor.Height / profileH : 1.0;

            var x = anchor.X + (int)Math.Round((slot.X - profileMinX) * scaleX);
            var y = anchor.Y + (int)Math.Round((slot.Y - profileMinY) * scaleY);
            var w = (int)Math.Round(slot.Width * scaleX);
            var h = (int)Math.Round(slot.Height * scaleY);

            if (UsePhysicalPixels)
                return new WindowRect { X = x, Y = y, Width = w, Height = h };

            var mon = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
                ?? Monitors.FirstOrDefault(m => m.IsPrimary)
                ?? Monitors.FirstOrDefault();
            var dpi = mon is null ? 1.0 : mon.DpiX / 96.0;
            return new WindowRect
            {
                X = (int)Math.Round(x * dpi),
                Y = (int)Math.Round(y * dpi),
                Width = (int)Math.Round(w * dpi),
                Height = (int)Math.Round(h * dpi)
            };
        }

        // 3. No target monitor resolved — fall back to the slot's literal coordinates.
        var rect = slot.ToRect();
        if (UsePhysicalPixels) return rect;

        var monitor = Monitors.FirstOrDefault(m => !string.IsNullOrWhiteSpace(slot.MonitorId) && m.Id == slot.MonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var scale = monitor is null ? 1.0 : monitor.DpiX / 96.0;
        return new WindowRect
        {
            X = (int)Math.Round(rect.X * scale),
            Y = (int)Math.Round(rect.Y * scale),
            Width = (int)Math.Round(rect.Width * scale),
            Height = (int)Math.Round(rect.Height * scale)
        };
    }

    private WindowRect? ResolveLayoutAnchor()
    {
        if (string.IsNullOrWhiteSpace(LayoutTargetMonitorId)) return null;
        var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId);
        if (monitor is null) return null;
        // Global setting OR the active profile's own per-profile "Avoid taskbar" flag fits the layout
        // into the work area instead of the full monitor bounds.
        var avoidTaskbar = UseMonitorWorkArea || SelectedProfile?.AvoidTaskbar == true;
        return avoidTaskbar ? monitor.WorkArea : monitor.Bounds;
    }

    private WindowRect ResolveCapturedRect(EveWindowInfo window)
    {
        var rect = window.Rect.Clone();
        if (UsePhysicalPixels) return rect;

        var monitor = Monitors.FirstOrDefault(m => m.Id == window.MonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var scale = monitor is null || monitor.DpiX == 0 ? 1.0 : monitor.DpiX / 96.0;
        return new WindowRect
        {
            X = (int)Math.Round(rect.X / scale),
            Y = (int)Math.Round(rect.Y / scale),
            Width = (int)Math.Round(rect.Width / scale),
            Height = (int)Math.Round(rect.Height / scale)
        };
    }

    // ── Warnings ───────────────────────────────────────────────────────────────

    private void WarnIfAssignedWindowsExceedUniquePositions(LayoutProfile profile)
    {
        var assignedSlots = Assignments
            .Where(a => a.AssignedWindows.Count > 0)
            .Select(a => profile.Slots.FirstOrDefault(s => s.SlotNumber == a.SlotNumber))
            .Where(s => s is not null).Cast<LayoutSlot>().ToList();
        var totalWindows = Assignments.Sum(a => a.AssignedWindows.Count);
        var uniquePositions = assignedSlots.Select(s => $"{s.X},{s.Y},{s.Width},{s.Height}").Distinct(StringComparer.Ordinal).Count();
        if (totalWindows > uniquePositions)
            Log.Warn($"Active profile has {uniquePositions} unique position(s) for {totalWindows} assigned client(s); some windows will stack/overlap intentionally.");
    }

    private void WarnIfProfileExceedsTarget(LayoutProfile profile)
    {
        var anchor = ResolveLayoutAnchor();
        if (anchor is null || profile.Slots.Count == 0 || !profile.IsBuiltIn) return;

        var minX = profile.Slots.Min(s => s.X);
        var minY = profile.Slots.Min(s => s.Y);
        var width = profile.Slots.Max(s => s.X + s.Width) - minX;
        var height = profile.Slots.Max(s => s.Y + s.Height) - minY;
        if (width > anchor.Width || height > anchor.Height)
            Log.Warn($"Profile area {width}x{height} is larger than target {(UseMonitorWorkArea ? "work area" : "monitor")} {anchor.Width}x{anchor.Height}; windows may spill onto another display.");
    }
}
