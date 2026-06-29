using System.ComponentModel;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using EveWindowCommander.Models;
using EveWindowCommander.Services;

namespace EveWindowCommander.ViewModels;

public sealed partial class MainWindowViewModel
{
    // Seat identities for the Hotkeys-tab switch picker. In Model A each slot is a fixed account
    // "seat" and its Label is the seat's main character — a stable, user-set identity that no longer
    // gets swapped between slots, so the picker lists seat labels directly.
    public IEnumerable<string> CharacterNames =>
        Assignments
            .Select(a => a.Label)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    // Same seat labels as CharacterNames but carrying the main character's portrait, for the
    // hotkey picker dropdown. First seat wins when two seats share a label.
    public IEnumerable<CharacterIdentity> CharacterIdentities =>
        Assignments
            .Where(a => !string.IsNullOrWhiteSpace(a.Label))
            .GroupBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CharacterIdentity(g.Key, g.First().PortraitUrl))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    // ── Master-seat branding (title bar) ───────────────────────────────────────

    public SlotAssignment? MasterSeatAssignment =>
        Assignments.FirstOrDefault(a => a.SlotNumber == ActiveMasterSeat);

    // The master account's display name: its ESI main character if linked, else the seat label.
    public string MasterCharacterName =>
        MasterSeatAssignment?.MainCharacter?.CharacterName
        ?? MasterSeatAssignment?.Label
        ?? "";

    public string MasterPortraitUrl => MasterSeatAssignment?.PortraitUrl ?? "";
    public bool HasMasterPortrait => !string.IsNullOrEmpty(MasterPortraitUrl);
    public bool HasMasterCharacter => !string.IsNullOrWhiteSpace(MasterCharacterName);

    // Raise everything the title-bar branding + hotkey picker depend on.
    internal void RaiseIdentityDependents()
    {
        OnPropertyChanged(nameof(CharacterNames));
        OnPropertyChanged(nameof(CharacterIdentities));
        OnPropertyChanged(nameof(MasterSeatAssignment));
        OnPropertyChanged(nameof(MasterCharacterName));
        OnPropertyChanged(nameof(MasterPortraitUrl));
        OnPropertyChanged(nameof(HasMasterPortrait));
        OnPropertyChanged(nameof(HasMasterCharacter));
    }

    // "EVE - Character Name" → "Character Name". Leaves non-EVE titles untouched.
    internal static string CharacterNameFromTitle(string title)
    {
        const string prefix = "EVE - ";
        return title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? title[prefix.Length..].Trim()
            : title;
    }

    // ── Window / slot management ───────────────────────────────────────────────

    private void AssignSelected()
    {
        if (SelectedWindow is null || SelectedAssignment is null) return;
        AssignWindowToSlot(SelectedAssignment);
    }

    private void AssignWindowToSlot(object? parameter)
    {
        if (parameter is not SlotAssignment assignment) return;
        if (SelectedWindow is null)
        {
            Log.Warn("Select a detected window before assigning it to a slot.");
            return;
        }

        var title = SelectedWindow.Title;

        if (assignment.AssignedWindows.Any(e => e.Title.Equals(title, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Warn($"'{title}' is already assigned to slot {assignment.SlotNumber}.");
            return;
        }

        // Remove from any other slot.
        foreach (var other in Assignments.Where(a => a != assignment))
        {
            var dup = other.AssignedWindows.FirstOrDefault(e => e.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
            if (dup is not null) other.AssignedWindows.Remove(dup);
        }

        assignment.AssignedWindows.Add(new SlotWindowEntry
        {
            Title = title,
            LastProcessId = SelectedWindow.ProcessId,
            LastHandleHex = SelectedWindow.HandleHex
        });

        // 2f — Auto-label slot from EVE character name when slot still has default name.
        TryAutoLabelSlot(assignment, SelectedWindow);

        Log.Info($"Added '{title}' to slot {assignment.SlotNumber} ({assignment.Label}).");
        Save();
    }

    private void RemoveWindowFromSlot(object? parameter)
    {
        if (parameter is not SlotWindowEntry entry) return;
        var slot = Assignments.FirstOrDefault(a => a.AssignedWindows.Contains(entry));
        if (slot is null) return;
        slot.AssignedWindows.Remove(entry);
        Log.Info($"Removed '{entry.Title}' from slot {slot.SlotNumber}.");
        Save();
    }

    private void ClearAssignment(SlotAssignment assignment)
    {
        assignment.AssignedWindows.Clear();
        Save();
    }

    private void AddSlot()
    {
        var nextSlotNumber = Assignments.Count == 0 ? 1 : Assignments.Max(a => a.SlotNumber) + 1;
        var assignment = new SlotAssignment { SlotNumber = nextSlotNumber, Label = $"Slot {nextSlotNumber}" };
        Assignments.Add(assignment);

        foreach (var profile in Profiles.Where(p => !p.IsBuiltIn && !p.Slots.Any(s => s.SlotNumber == nextSlotNumber)))
        {
            var template = profile.Slots.OrderBy(s => s.SlotNumber).LastOrDefault();
            profile.Slots.Add(new LayoutSlot
            {
                SlotNumber = nextSlotNumber,
                Label = assignment.Label,
                MonitorId = template?.MonitorId ?? "",
                X = template?.X ?? 0,
                Y = template?.Y ?? 0,
                Width = template?.Width ?? 1280,
                Height = template?.Height ?? 720,
                Borderless = template?.Borderless ?? true
            });
        }

        SelectedAssignment = assignment;
        Save();
        OnPropertyChanged(nameof(Assignments));
        OnPropertyChanged(nameof(ActiveProfileSlots));
        RebuildLayoutPreview();
        Log.Info($"Added slot {nextSlotNumber}.");
    }

    private void DeleteSelectedSlot()
    {
        if (SelectedAssignment is null || Assignments.Count <= 1) return;

        var result = MessageBox.Show(
            $"Delete slot {SelectedAssignment.SlotNumber} ({SelectedAssignment.Label})?",
            "Delete Slot", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var removedSlotNumber = SelectedAssignment.SlotNumber;
        var removedLabel = SelectedAssignment.Label;
        Assignments.Remove(SelectedAssignment);

        foreach (var a in Assignments.Where(a => a.SlotNumber > removedSlotNumber).OrderBy(a => a.SlotNumber))
            a.SlotNumber--;

        foreach (var profile in Profiles.Where(p => !p.IsBuiltIn))
        {
            var matching = profile.Slots.FirstOrDefault(s => s.SlotNumber == removedSlotNumber);
            if (matching is not null) profile.Slots.Remove(matching);
            foreach (var slot in profile.Slots.Where(s => s.SlotNumber > removedSlotNumber).OrderBy(s => s.SlotNumber))
                slot.SlotNumber--;

            // Keep the per-profile master + each slot's home-seat reference aligned with the renumbering.
            if (profile.MasterSeat == removedSlotNumber) profile.MasterSeat = 0;        // 0 = fall back to centre
            else if (profile.MasterSeat > removedSlotNumber) profile.MasterSeat--;

            foreach (var slot in profile.Slots)
            {
                if (slot.HomeSeat == removedSlotNumber) slot.HomeSeat = null;
                else if (slot.HomeSeat > removedSlotNumber) slot.HomeSeat--;
            }
        }

        SelectedAssignment = Assignments.FirstOrDefault(a => a.SlotNumber == Math.Min(removedSlotNumber, Assignments.Count))
            ?? Assignments.LastOrDefault();

        // If the master slot was deleted, reset master to slot 1 (or the first remaining slot).
        if (ActiveMasterSeat == removedSlotNumber || Assignments.All(a => a.SlotNumber != ActiveMasterSeat))
        {
            ActiveMasterSeat = Assignments.FirstOrDefault()?.SlotNumber ?? 1;
            OnPropertyChanged(nameof(MasterSlotNumber));
        }
        SyncMasterSlot();

        Save();
        OnPropertyChanged(nameof(Assignments));
        OnPropertyChanged(nameof(ActiveProfileSlots));
        RebuildLayoutPreview();
        Log.Info($"Deleted slot {removedSlotNumber} ({removedLabel}).");
    }

    // 2e — Assign all unassigned detected windows to empty slots in order.
    private void AutoAssignAll()
    {
        var unassigned = Windows.Where(w => !IsWindowAssigned(w)).ToList();
        if (unassigned.Count == 0) { Log.Warn("No unassigned windows to auto-assign."); return; }
        var emptySlots = Assignments.Where(a => a.AssignedWindows.Count == 0).ToList();
        if (emptySlots.Count == 0) { Log.Warn("No empty slots available for auto-assign."); return; }

        var count = 0;
        foreach (var (window, slot) in unassigned.Zip(emptySlots))
        {
            slot.AssignedWindows.Add(new SlotWindowEntry
            {
                Title = window.Title,
                LastProcessId = window.ProcessId,
                LastHandleHex = window.HandleHex
            });
            TryAutoLabelSlot(slot, window);
            count++;
        }

        Save();
        Log.Info($"Auto-assigned {count} window(s) to slots.");
    }

    // 3d — Assign the current foreground EVE window to the next empty slot.
    private void QuickAssignActive()
    {
        Refresh();
        var active = FindActiveManagedWindow();
        if (active is null) { Log.Warn("Active window is not a detected EVE/test window."); return; }
        if (IsWindowAssigned(active)) { Log.Warn($"'{active.Title}' is already assigned to a slot."); return; }

        var emptySlot = Assignments.FirstOrDefault(a => a.AssignedWindows.Count == 0);
        if (emptySlot is null) { Log.Warn("No empty slots available."); return; }

        emptySlot.AssignedWindows.Add(new SlotWindowEntry
        {
            Title = active.Title,
            LastProcessId = active.ProcessId,
            LastHandleHex = active.HandleHex
        });
        TryAutoLabelSlot(emptySlot, active);
        Save();
        Status = $"Quick-assigned '{active.Title}' to slot {emptySlot.SlotNumber}.";
        Log.Info(Status);
    }

    // 2f — Populate slot label from EVE character name when slot still has default "Slot N" label.
    private static void TryAutoLabelSlot(SlotAssignment slot, EveWindowInfo window)
    {
        var isDefault = string.IsNullOrEmpty(slot.Label)
            || slot.Label.Equals($"Slot {slot.SlotNumber}", StringComparison.Ordinal);
        if (!isDefault) return;
        const string evePrefix = "EVE - ";
        if (window.Title?.StartsWith(evePrefix, StringComparison.OrdinalIgnoreCase) == true)
            slot.Label = window.Title[evePrefix.Length..].Trim();
    }

    // ── Window lookup ──────────────────────────────────────────────────────────

    // 1d — PID-based fallback before broad substring match.
    private EveWindowInfo? FindWindowByEntry(SlotWindowEntry entry)
    {
        var exact = Windows.FirstOrDefault(w => w.Title.Equals(entry.Title, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        if (entry.LastProcessId.HasValue && entry.LastProcessId.Value > 0)
        {
            var byPid = Windows.FirstOrDefault(w => w.ProcessId == entry.LastProcessId.Value);
            if (byPid is not null) return byPid;
        }

        return Windows.FirstOrDefault(w => w.Title.Contains(entry.Title, StringComparison.OrdinalIgnoreCase));
    }

    private EveWindowInfo? FindWindowByTitle(string title)
        => Windows.FirstOrDefault(w => w.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
           ?? Windows.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<EveWindowInfo> FindAssignedWindows(SlotAssignment assignment)
        => assignment.AssignedWindows
            .Select(entry => FindWindowByEntry(entry))
            .Where(w => w is not null)
            .Cast<EveWindowInfo>();

    private EveWindowInfo? FindActiveManagedWindow()
    {
        var handle = _windowService.GetForegroundWindowHandle();
        return Windows.FirstOrDefault(w => w.Handle == handle);
    }

    private bool IsWindowAssigned(EveWindowInfo w)
        => Assignments.Any(a => a.AssignedWindows.Any(e => e.Title.Equals(w.Title, StringComparison.OrdinalIgnoreCase)));

    private void RebindRestartedWindows()
    {
        foreach (var assignment in Assignments)
        {
            foreach (var entry in assignment.AssignedWindows)
            {
                var window = FindWindowByEntry(entry);
                if (window is null) continue;
                entry.LastProcessId = window.ProcessId;
                entry.LastHandleHex = window.HandleHex;
            }
        }
    }

    // Compare the set of detected, slot-assigned EVE windows against the previous refresh. When a
    // client that was absent reappears (e.g. the user closed all clients and relaunched them), arm
    // the debounce timer so the active profile is re-applied automatically once the clients settle.
    private void DetectNewlyLaunchedClients()
    {
        var currentAssigned = Windows
            .Where(IsWindowAssigned)
            .Select(w => w.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // First refresh only seeds the baseline — clients already open at launch are left in place
        // (use "Apply a layout profile on startup" to position those). Auto-apply only fires for
        // clients that appear *after* EWC is running.
        var firstRefresh = !_clientBaselineInitialized;
        _clientBaselineInitialized = true;

        var newlyAppeared = currentAssigned.Where(t => !_knownAssignedTitles.Contains(t)).ToList();

        // Replace the baseline so a client must actually disappear and return to re-trigger.
        _knownAssignedTitles.Clear();
        foreach (var title in currentAssigned) _knownAssignedTitles.Add(title);

        if (firstRefresh) return;
        if (!_settings.AutoApplyOnClientLaunch) return;
        if (newlyAppeared.Count == 0 || _applyInProgress || SelectedProfile is null) return;

        // (Re)start the debounce; further launches within the window push the apply back.
        _autoApplyTimer.Stop();
        _autoApplyTimer.Start();
    }

    internal void SyncMasterSlot()
    {
        foreach (var a in Assignments)
            a.IsMaster = a.SlotNumber == ActiveMasterSeat;
    }

    // Apply a master seat chosen during the setup wizard (the first character linked there).
    public void SetMasterSeatNumber(int seatNumber)
    {
        var seat = Assignments.FirstOrDefault(a => a.SlotNumber == seatNumber);
        if (seat is not null) SetMasterSlot(seat);
    }

    private void SetMasterSlot(object? parameter)
    {
        if (parameter is not SlotAssignment assignment) return;
        ActiveMasterSeat = assignment.SlotNumber;
        SyncMasterSlot();
        var masterIdx = Assignments.IndexOf(assignment);
        if (masterIdx > 0) Assignments.Move(masterIdx, 0);
        OnPropertyChanged(nameof(MasterSlotNumber));
        RaiseIdentityDependents();
        Save();
        ReapplyCornerHomes();   // re-baseline corners + refresh mini-map/overlays for the new master
        Log.Info($"Slot {assignment.SlotNumber} ({assignment.Label}) set as master.");
    }

    private void SyncProfileSlotLabel(SlotAssignment assignment)
    {
        foreach (var profile in Profiles.Where(p => !p.IsBuiltIn))
        {
            var slot = profile.Slots.FirstOrDefault(s => s.SlotNumber == assignment.SlotNumber);
            if (slot is not null) slot.Label = assignment.Label;
        }
        RebuildLayoutPreview();
    }

    // ── Subscriptions ──────────────────────────────────────────────────────────

    private void SubscribeToAssignmentChanges()
    {
        foreach (var a in Assignments)
        {
            a.PropertyChanged += OnAssignmentPropertyChanged;
            a.AssignedWindows.CollectionChanged += OnAssignedWindowsChanged;
        }
        Assignments.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (SlotAssignment a in e.NewItems)
                {
                    a.PropertyChanged += OnAssignmentPropertyChanged;
                    a.AssignedWindows.CollectionChanged += OnAssignedWindowsChanged;
                }
            if (e.OldItems is not null)
                foreach (SlotAssignment a in e.OldItems)
                {
                    a.PropertyChanged -= OnAssignmentPropertyChanged;
                    a.AssignedWindows.CollectionChanged -= OnAssignedWindowsChanged;
                }
            WindowsView.Refresh();
            RaiseIdentityDependents();
        };
    }

    private void OnAssignmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SlotAssignment.Label))
        {
            ScheduleAutoSave();
            RaiseIdentityDependents();
            RebuildMiniMap();
        }
    }

    private void OnAssignedWindowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        WindowsView.Refresh();
        RaiseIdentityDependents();
        RebuildMiniMap();
    }
}
