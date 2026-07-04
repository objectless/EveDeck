using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using EveDeck.Models;
using EveDeck.Services;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ── Global hotkey routing ──────────────────────────────────────────────────

    public void HandleHotkey(string actionId)
    {
        try
        {
            SafetyGuard.ThrowIfInputBroadcastAction(actionId);

            // When enabled, only fire hotkeys while an EVE/test window is in the foreground.
            // FocusSlot and SwitchToCharacter are exempted — they're the primary ways to bring an
            // EVE client to focus from elsewhere (e.g. the user's Tartarus F-keys).
            if (_settings.RequireEveFocusForHotkeys
                && !actionId.StartsWith("FocusSlot", StringComparison.OrdinalIgnoreCase)
                && !actionId.StartsWith("FocusDirection", StringComparison.OrdinalIgnoreCase)
                && !actionId.StartsWith("SwitchToCharacter", StringComparison.OrdinalIgnoreCase))
            {
                var fg = _windowService.GetForegroundWindowHandle();
                if (!Windows.Any(w => w.Handle == fg)) return;
            }

            if (actionId.StartsWith("FocusSlot", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(actionId["FocusSlot".Length..], out var focusSlot))
            {
                FocusSlot(focusSlot);
            }
            else if (actionId.Equals("CycleNext", StringComparison.OrdinalIgnoreCase)) Cycle(1);
            else if (actionId.Equals("CyclePrevious", StringComparison.OrdinalIgnoreCase)) Cycle(-1);
            else if (actionId.Equals("ApplyLayout", StringComparison.OrdinalIgnoreCase)) ApplyActiveProfile();
            else if (actionId.Equals("ToggleBorderless", StringComparison.OrdinalIgnoreCase)) ToggleActiveBorderless();
            else if (actionId.Equals("RestoreActiveStyle", StringComparison.OrdinalIgnoreCase)) RestoreActiveStyle();
            else if (actionId.Equals("QuickAssignActive", StringComparison.OrdinalIgnoreCase)) QuickAssignActive();   // 3d
            else if (actionId.Equals("UndoLastApply", StringComparison.OrdinalIgnoreCase)) UndoLastApply();           // 3b
            else if (actionId.StartsWith("MoveActiveToSlot", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(actionId["MoveActiveToSlot".Length..], out var moveSlot)) MoveActiveToSlot(moveSlot);
            else if (actionId.StartsWith("SwapActiveWithSlot", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(actionId["SwapActiveWithSlot".Length..], out var swapSlot)) SwapActiveWithSlot(swapSlot);
            else if (actionId.Equals("SwapFocusedWithMaster", StringComparison.OrdinalIgnoreCase)) SwapFocusedWithMaster();
            else if (actionId.StartsWith("SwapSlotWithMaster", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(actionId["SwapSlotWithMaster".Length..], out var masterSwapSlot)) SwapSlotWithMaster(masterSwapSlot);
            else if (actionId.StartsWith("FocusDirection", StringComparison.OrdinalIgnoreCase))
                FocusDirection(actionId["FocusDirection".Length..]);
            else if (actionId.StartsWith("SwitchToCharacter", StringComparison.OrdinalIgnoreCase))
            {
                var binding = Hotkeys.FirstOrDefault(h => h.ActionId.Equals(actionId, StringComparison.OrdinalIgnoreCase));
                SwitchToCharacter(binding?.TargetCharacter);
            }
            else if (actionId.Equals("ToggleTopmost", StringComparison.OrdinalIgnoreCase))
            {
                ToggleTopmostForActive();
            }
            else if (actionId.StartsWith("SwitchCharacterSet", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(actionId["SwitchCharacterSet".Length..], out var setIndex))
            {
                SwitchCharacterSet(setIndex);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Hotkey {actionId} failed: {ex.Message}");
        }
    }

    // ── Hotkey capture ─────────────────────────────────────────────────────────

    public bool TryCompleteHotkeyCapture(Key key, ModifierKeys modifiers)
    {
        if (_capturingHotkey is null) return false;

        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return true;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            Status = "Press a non-modifier key with Ctrl, Alt, or Shift.";
            return true;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) { Status = "That key cannot be used as a global hotkey."; return true; }

        var modifierFlags = ToHotkeyModifiers(modifiers);

        // 2b — Proactive conflict detection before accepting the new gesture.
        var gestureKey = $"{modifierFlags}:{virtualKey}";
        var conflict = Hotkeys.FirstOrDefault(h =>
            h != _capturingHotkey && h.Enabled && h.VirtualKey != 0
            && $"{h.Modifiers}:{h.VirtualKey}" == gestureKey);
        if (conflict is not null)
        {
            Status = $"Conflict: {FormatGesture(modifierFlags, key)} is already used by '{conflict.DisplayName}'. Choose a different combination or clear the other binding first.";
            return true; // swallow key but keep capture active
        }

        _capturingHotkey.Modifiers = modifierFlags;
        _capturingHotkey.VirtualKey = virtualKey;
        _capturingHotkey.GestureText = FormatGesture(modifierFlags, key);
        Status = $"Set {_capturingHotkey.DisplayName} to {_capturingHotkey.GestureText}.";
        Log.Info(Status);

        // A modifier-less key is consumed by EVE too (incl. chat) whenever EVE is focused. Warn but
        // still accept it — some users deliberately bind macro-pad / Tartarus keys this way.
        if (modifierFlags == 0)
        {
            Status = $"Set {_capturingHotkey.DisplayName} to {_capturingHotkey.GestureText}. ⚠ Single key with no Ctrl/Alt/Shift — it's intercepted inside EVE too (including chat). Add a modifier unless you're using a dedicated macro key.";
            Log.Warn($"Hotkey bound to a modifier-less key ({key}); it will be captured inside EVE whenever EVE is focused.");
        }
        _capturingHotkey.IsCapturing = false;
        _capturingHotkey = null;
        OnPropertyChanged(nameof(IsCapturingHotkey));
        Save();
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Abandon a pending hotkey capture. CRITICAL: while a capture is pending ALL global hotkeys are
    // unregistered (so the pressed combo reaches WPF instead of firing an action) - if the capture is
    // never completed or cancelled, every hotkey in the app stays dead. The main window calls this on
    // Deactivated/tab-switch so clicking away from an armed capture can't strand the hotkeys.
    public void CancelHotkeyCapture()
    {
        if (_capturingHotkey is null) return;
        Status = "Hotkey capture cancelled.";
        _capturingHotkey.IsCapturing = false;
        _capturingHotkey = null;
        OnPropertyChanged(nameof(IsCapturingHotkey));
    }

    private void BeginHotkeyCapture(object? parameter)
    {
        var binding = parameter as HotkeyBinding ?? SelectedHotkey;
        if (binding is null) return;
        if (_capturingHotkey is not null && _capturingHotkey != binding)
            _capturingHotkey.IsCapturing = false;
        _capturingHotkey = binding;
        binding.IsCapturing = true;
        Status = $"Press the new key combo for {binding.DisplayName}. Press Esc to cancel.";
        OnPropertyChanged(nameof(IsCapturingHotkey));
    }

    private void ClearHotkey(object? parameter)
    {
        var binding = parameter as HotkeyBinding ?? SelectedHotkey;
        if (binding is null) return;
        binding.Modifiers = 0;
        binding.VirtualKey = 0;
        binding.GestureText = "";
        Save();
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
        Log.Info($"Cleared hotkey for {binding.DisplayName}.");
    }

    // 2c — Reset all hotkey bindings to their factory defaults.
    private void ResetHotkeysToDefaults()
    {
        var result = MessageBox.Show(
            "Reset all hotkeys to their defaults?",
            "Reset Hotkeys", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        var defaults = HotkeyDefaults.Create();
        Hotkeys.Clear();
        foreach (var binding in defaults) Hotkeys.Add(binding);
        Save();
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
        Log.Info("Hotkeys reset to defaults.");
    }

    // ── Focus / cycle ──────────────────────────────────────────────────────────

    private void FocusSlot(int slotNumber)
    {
        var assignment = Assignments.FirstOrDefault(a => a.SlotNumber == slotNumber);
        if (assignment is null) return;
        var windows = FindAssignedWindows(assignment).ToList();
        if (windows.Count == 0) { Log.Warn($"Slot {slotNumber} has no running assigned windows."); return; }

        _lastFocusedHandle.TryGetValue(slotNumber, out var lastHandle);
        var lastIdx = windows.FindIndex(w => w.Handle == lastHandle);
        var nextIdx = windows.Count == 1 ? 0 : (lastIdx < 0 ? 0 : (lastIdx + 1) % windows.Count);
        var next = windows[nextIdx];
        _windowService.FocusWindow(next.Handle);
        _lastFocusedHandle[slotNumber] = next.Handle;
        Log.Info($"Focused slot {slotNumber}: {next.Title}.");
    }

    private void Cycle(int direction)
    {
        var assignedWindows = Assignments.SelectMany(FindAssignedWindows).ToList();
        if (assignedWindows.Count == 0) { Log.Warn("No assigned windows are available to cycle."); return; }

        var activeHandle = _windowService.GetForegroundWindowHandle();
        var current = assignedWindows.FindIndex(w => w.Handle == activeHandle);
        var next = current < 0 ? 0 : (current + direction + assignedWindows.Count) % assignedWindows.Count;
        _windowService.FocusWindow(assignedWindows[next].Handle);
        Log.Info($"Cycled focus to {assignedWindows[next].Title}.");
    }

    // Resolve the slot at the given grid-direction and bring it to the centre (overlay mode) or
    // foreground (flat mode). Works for any profile: 5-client uses TL/TR/BL/BR/C; 6+ fills more.
    private void FocusDirection(string dir)
    {
        var profile = SelectedProfile;
        if (profile is null || profile.Slots.Count == 0) return;

        var (targetRow, targetCol) = dir.ToUpperInvariant() switch
        {
            "TL" => ("T", "L"),
            "TC" => ("T", "C"),
            "TR" => ("T", "R"),
            "ML" => ("M", "L"),
            "C"  => ("M", "C"),
            "MR" => ("M", "R"),
            "BL" => ("B", "L"),
            "BC" => ("B", "C"),
            "BR" => ("B", "R"),
            _    => ("?", "?")
        };
        if (targetRow == "?") return;

        var minX   = profile.Slots.Min(s => s.X);
        var minY   = profile.Slots.Min(s => s.Y);
        var totalW = Math.Max(1, profile.Slots.Max(s => s.X + s.Width)  - minX);
        var totalH = Math.Max(1, profile.Slots.Max(s => s.Y + s.Height) - minY);

        var positionId = profile.Slots
            .Where(s => GridBucket(s, minX, minY, totalW, totalH) == (targetRow, targetCol))
            .OrderBy(s => s.SlotNumber)
            .Select(s => s.SlotNumber)
            .FirstOrDefault(0);

        if (positionId == 0) return; // no slot in that direction — silent no-op

        int seat;
        if (_settings.CornerOverlaysEnabled && profile.SupportsCornerGrid)
        {
            if (_centeredSeatByGroup.Count == 0) ResetCornerOccupancy();
            seat = OccupantAtPosition(positionId);
        }
        else
        {
            seat = positionId;
        }

        CenterSeat(seat);
    }

    // ── Borderless / style ─────────────────────────────────────────────────────

    private void ToggleSelectedBorderless()
    {
        if (SelectedWindow is null) return;
        ToggleBorderless(SelectedWindow);
    }

    private void ToggleActiveBorderless()
    {
        Refresh();
        var active = FindActiveManagedWindow();
        if (active is null) { Log.Warn("Active window is not a detected EVE/test window."); return; }
        ToggleBorderless(active);
    }

    private void ToggleBorderless(EveWindowInfo window)
    {
        try
        {
            if (window.IsBorderless) RestoreStyle(window);
            else
            {
                SaveStyleSnapshotIfMissing(window);
                _windowService.MakeBorderless(window.Handle);
                Log.Info($"Made {window.Title} borderless.");
            }
            Refresh();
        }
        catch (Exception ex) { Log.Error($"Borderless toggle failed for {window.Title}: {ex.Message}"); }
    }

    private void RestoreSelectedStyle()
    {
        if (SelectedWindow is not null) { RestoreStyle(SelectedWindow); Refresh(); }
    }

    private void RestoreActiveStyle()
    {
        Refresh();
        var active = FindActiveManagedWindow();
        if (active is not null) { RestoreStyle(active); Refresh(); }
    }

    // 1a — Prefer HWND-keyed session snapshot; fall back to persisted title-keyed snapshot.
    private void RestoreStyle(EveWindowInfo window)
    {
        if (_sessionSnapshots.TryGetValue(window.Handle, out var sessionSnap))
        {
            _windowService.RestoreStyle(window.Handle, sessionSnap);
            Log.Info($"Restored normal style for {window.Title}.");
            return;
        }
        if (_settings.StyleSnapshotsByTitle.TryGetValue(window.Title, out var persistedSnap))
        {
            _windowService.RestoreStyle(window.Handle, persistedSnap);
            _sessionSnapshots[window.Handle] = persistedSnap;
            Log.Info($"Restored normal style for {window.Title}.");
            return;
        }
        Log.Warn($"No saved style snapshot exists for {window.Title}.");
    }

    // 1a — Capture style into session dictionary first, then persist by title.
    private void SaveStyleSnapshotIfMissing(EveWindowInfo window)
    {
        if (_sessionSnapshots.ContainsKey(window.Handle)) return;
        if (_settings.StyleSnapshotsByTitle.TryGetValue(window.Title, out var existing))
        {
            _sessionSnapshots[window.Handle] = existing;
            return;
        }
        var snap = _windowService.CaptureStyle(window.Handle, window.Title);
        _sessionSnapshots[window.Handle] = snap;
        _settings.StyleSnapshotsByTitle[window.Title] = snap;
        Save();
    }

    // ── Subscriptions ──────────────────────────────────────────────────────────

    private void SubscribeToHotkeyChanges()
    {
        foreach (var h in Hotkeys) h.PropertyChanged += OnHotkeyPropertyChanged;
        Hotkeys.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (HotkeyBinding h in e.NewItems) h.PropertyChanged += OnHotkeyPropertyChanged;
            if (e.OldItems is not null)
                foreach (HotkeyBinding h in e.OldItems) h.PropertyChanged -= OnHotkeyPropertyChanged;
        };
    }

    private void OnHotkeyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HotkeyBinding.Enabled))
        {
            Save();
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.PropertyName == nameof(HotkeyBinding.TargetCharacter))
        {
            Save();
        }
    }
}
