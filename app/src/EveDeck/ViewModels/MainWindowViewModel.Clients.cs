using System.ComponentModel;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using EveDeck.Models;
using EveDeck.Services;

namespace EveDeck.ViewModels;

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
            .Select(g => new CharacterIdentity(g.Key, g.First().MainCharacter?.Portrait))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    // ── Master-seat branding (title bar) ───────────────────────────────────────

    public SlotAssignment? MasterSeatAssignment =>
        Assignments.FirstOrDefault(a => a.SlotNumber == ActiveMasterSeat);

    // The master account's title-bar name: the character actually logged into the master seat's live
    // window right now, else its ESI main character if linked, else the seat label.
    public string MasterCharacterName
    {
        get
        {
            var seat = MasterSeatAssignment;
            if (seat is null) return "";
            return string.IsNullOrWhiteSpace(seat.RunningCharacterName)
                ? (seat.MainCharacter?.CharacterName ?? seat.Label)
                : seat.RunningCharacterName;
        }
    }

    // The master's title-bar portrait follows the same character MasterCharacterName resolves to --
    // whoever is actually logged into the master seat right now, else its ESI main.
    public CharacterPortrait? MasterPortrait => MasterSeatAssignment?.RunningPortrait;
    public bool HasMasterPortrait => MasterPortrait is not null;
    public bool HasMasterCharacter => !string.IsNullOrWhiteSpace(MasterCharacterName);

    // Raise everything the title-bar branding + hotkey picker depend on.
    internal void RaiseIdentityDependents()
    {
        OnPropertyChanged(nameof(CharacterNames));
        OnPropertyChanged(nameof(CharacterIdentities));
        OnPropertyChanged(nameof(MasterSeatAssignment));
        OnPropertyChanged(nameof(MasterCharacterName));
        OnPropertyChanged(nameof(MasterPortrait));
        OnPropertyChanged(nameof(HasMasterPortrait));
        OnPropertyChanged(nameof(HasMasterCharacter));
        OnPropertyChanged(nameof(PiConsolidationOptions));
        OnPropertyChanged(nameof(PiConsolidationSelection));
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

        if (assignment.IsTopmost)
            _windowService.SetWindowTopmost(SelectedWindow.Handle, IsEveWindowForeground());

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
            if (profile.MasterSeat == removedSlotNumber) profile.MasterSeat = 0;        // 0 = fall back to center
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
            OnPropertyChanged(nameof(MasterSeatSummary));
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

        // Pair once, then preview and apply from that SAME plan. Recomputing the pairing after the
        // prompt could apply a different mapping than the one shown, because windows come and go.
        var plan = unassigned.Zip(emptySlots, (window, slot) => (Window: window, Slot: slot)).ToList();
        if (!ConfirmAutoAssignPlan(plan, unassigned.Count, emptySlots.Count)) return;

        foreach (var (window, slot) in plan)
        {
            slot.AssignedWindows.Add(new SlotWindowEntry
            {
                Title = window.Title,
                LastProcessId = window.ProcessId,
                LastHandleHex = window.HandleHex
            });
            TryAutoLabelSlot(slot, window);
        }

        Save();
        Log.Info($"Auto-assigned {plan.Count} window(s) to slots.");
    }

    // Shows the exact mapping AutoAssignAll is about to apply. Auto-assign only ever fills EMPTY
    // seats -- it never replaces an existing assignment -- so this is a "here is what you'll get"
    // preview rather than a destructive-action guard. It exists because the old behaviour applied
    // silently and undoing a wrong mapping meant reassigning every seat by hand.
    private static bool ConfirmAutoAssignPlan(
        IReadOnlyList<(EveWindowInfo Window, SlotAssignment Slot)> plan,
        int windowCount,
        int slotCount)
    {
        const int maxLines = 20;
        var lines = new List<string>
        {
            $"Auto-assign will link {plan.Count} window(s) to empty seats:",
            ""
        };

        foreach (var (window, slot) in plan.Take(maxLines))
            lines.Add($"    Seat {slot.SlotNumber} ({slot.DisplayLabel})  <-  {window.Title}");
        if (plan.Count > maxLines)
            lines.Add($"    ...and {plan.Count - maxLines} more.");

        var leftoverWindows = windowCount - plan.Count;
        var leftoverSlots = slotCount - plan.Count;
        if (leftoverWindows > 0 || leftoverSlots > 0)
        {
            lines.Add("");
            if (leftoverWindows > 0)
                lines.Add($"{leftoverWindows} detected window(s) will be left unassigned - no empty seats for them.");
            if (leftoverSlots > 0)
                lines.Add($"{leftoverSlots} seat(s) will stay empty - no windows left to fill them.");
        }

        lines.Add("");
        lines.Add("Apply this mapping?");

        return MessageBox.Show(
            string.Join(Environment.NewLine, lines),
            "Auto-Assign All", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
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
        // Stickiness first: if we already resolved this entry to a specific live window and that
        // exact window still exists AND still matches the entry's identity, keep it. A window's
        // handle is stable for its whole lifetime, so pinning to it stops the binding from bouncing
        // to a same-titled sibling client when EnumWindows reorders (it returns windows in Z-order,
        // which reshuffles on every focus change). That bounce was re-pointing corner-tile sources
        // and causing the previews to "randomly refresh."
        if (entry.ResolvedHandle != 0)
        {
            var sticky = Windows.FirstOrDefault(w => w.Handle == entry.ResolvedHandle);
            if (sticky is not null &&
                (sticky.Title.Equals(entry.Title, StringComparison.OrdinalIgnoreCase)
                 || (entry.LastProcessId is > 0 && sticky.ProcessId == entry.LastProcessId.Value)
                 || sticky.Title.Contains(entry.Title, StringComparison.OrdinalIgnoreCase)))
                return sticky;
        }

        // Exact title and PID are strong identity evidence -- they may claim a window even if another
        // entry currently points at it (that other entry is then simply wrong and will re-resolve).
        var match =
            Windows.FirstOrDefault(w => w.Title.Equals(entry.Title, StringComparison.OrdinalIgnoreCase))
            ?? (entry.LastProcessId is > 0 ? Windows.FirstOrDefault(w => w.ProcessId == entry.LastProcessId.Value) : null);

        // Substring is the WEAKEST match and must never steal a window another seat already holds.
        // Without this guard every seat collapses onto one client whenever titles stop being unique --
        // which is exactly what happens at the login screen, where EVE titles every client plainly
        // "EVE" (see IsAtLoginScreen). A seat whose stored entry title had degraded to something that
        // generic would substring-match the FIRST EVE window, and so would every other seat, leaving
        // all preview tiles registered against a single handle and mirroring one client.
        if (match is null)
        {
            var claimed = HandlesClaimedByOtherEntries(entry);
            match = Windows.FirstOrDefault(w =>
                        !claimed.Contains(w.Handle) && w.Title.Contains(entry.Title, StringComparison.OrdinalIgnoreCase));
        }

        entry.ResolvedHandle = match?.Handle ?? 0;
        return match;
    }

    // Window handles currently pinned by some OTHER seat's entry. Cheap enough to rebuild per call at
    // this scale (a handful of seats, each with a handful of entries) and always current, which
    // matters more here than the allocation -- a cached version going stale is the failure mode that
    // produced the mirroring bug in the first place.
    private HashSet<nint> HandlesClaimedByOtherEntries(SlotWindowEntry except)
    {
        var claimed = new HashSet<nint>();
        foreach (var assignment in Assignments)
            foreach (var other in assignment.AssignedWindows)
                if (!ReferenceEquals(other, except) && other.ResolvedHandle != 0)
                    claimed.Add(other.ResolvedHandle);
        return claimed;
    }

    private EveWindowInfo? FindWindowByTitle(string title)
        => Windows.FirstOrDefault(w => w.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
           ?? Windows.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<EveWindowInfo> FindAssignedWindows(SlotAssignment assignment)
        => assignment.AssignedWindows
            .Select(entry => FindWindowByEntry(entry))
            .Where(w => w is not null)
            .Cast<EveWindowInfo>();

    // Refresh each seat's live running-character name from the character logged into its first
    // detected window, so every label surface (title bar, minimap, corner pills, slot-card "Running:"
    // line) reflects whoever is actually playing that seat. Falls back to empty -> seat Label when the
    // seat has no live window. Call after RebindRestartedWindows so entry titles are current.
    private void UpdateLiveSeatCharacters()
    {
        foreach (var assignment in Assignments)
        {
            var window = FindSeatWindow(assignment.SlotNumber);
            assignment.RunningCharacterName = window is null ? "" : CharacterNameFromTitle(window.Title);
        }
    }

    private EveWindowInfo? FindActiveManagedWindow()
    {
        var handle = _windowService.GetForegroundWindowHandle();
        return Windows.FirstOrDefault(w => w.Handle == handle);
    }

    // Handles of windows on seats marked NeverMinimize — exempt from every bulk-minimize path.
    private HashSet<nint> ProtectedWindowHandles()
        => Assignments.Where(a => a.NeverMinimize)
            .SelectMany(FindAssignedWindows)
            .Select(w => w.Handle)
            .ToHashSet();

    // Boss key: minimize every EVE client at once, except protected seats. Window-state change
    // only (ShowWindow) — no input is ever sent to a client.
    private void MinimizeAllClients()
    {
        Refresh();
        var protectedHandles = ProtectedWindowHandles();
        var count = 0;
        foreach (var window in Windows)
        {
            if (protectedHandles.Contains(window.Handle)) continue;
            _windowService.MinimizeWindow(window.Handle);
            count++;
        }
        Log.Info($"Minimized {count} EVE client(s) ({protectedHandles.Count} protected seat window(s) skipped).");
    }

    // Optional eve-o-preview-style auto-minimize: whenever an EVE client takes the foreground,
    // minimize the others (except protected seats). Only active in flat layouts — corner-overlay
    // mode parks alts off-screen and needs them UNminimized so DWM keeps compositing their live
    // thumbnails (minimized windows stop rendering).
    internal void AutoMinimizeInactive(nint foregroundHwnd)
    {
        if (!_settings.AutoMinimizeInactiveClients || CornerOverlaysLive) return;
        if (!Windows.Any(w => w.Handle == foregroundHwnd)) return;

        var protectedHandles = ProtectedWindowHandles();
        foreach (var window in Windows)
        {
            if (window.Handle == foregroundHwnd || protectedHandles.Contains(window.Handle)) continue;
            if (_windowService.IsWindowMinimized(window.Handle)) continue;
            _windowService.MinimizeWindow(window.Handle);
        }
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

                // Only refresh PID/handle when the resolved window is the SAME character this entry
                // represents. FindWindowByEntry falls back to PID/substring matching, which on a
                // character-set seat (several pre-assigned alts sharing one physical client) cross-matches
                // a sibling character's window. Accepting that match -- and, worse, the old code then
                // overwriting entry.Title with the sibling's name -- permanently collapsed the set into
                // duplicates and silently dropped a character (settings.json corruption). A relaunched
                // SAME character still matches here by exact title. The entry's title is its stable
                // identity and is never rewritten; live per-seat labels come from UpdateLiveSeatCharacters.
                if (!CharacterNameFromTitle(window.Title).Equals(CharacterNameFromTitle(entry.Title), StringComparison.OrdinalIgnoreCase))
                    continue;

                entry.LastProcessId = window.ProcessId;
                entry.LastHandleHex = window.HandleHex;
            }
        }
    }

    // PID → character name seen on the previous refresh, for assigned windows. Lets us tell a plain
    // client relaunch (title disappears, new PID appears) apart from a character switch on the same
    // running client (same PID, title's character name changes after logging off and picking another).
    private readonly Dictionary<int, string> _lastKnownCharacterByPid = new();

    // Compare the set of detected, slot-assigned EVE windows against the previous refresh. When a
    // client that was absent reappears (e.g. the user closed all clients and relaunched them), or an
    // already-assigned client's character changes (logged off and picked a different one), arm the
    // debounce timer so the active profile is re-applied automatically once the clients settle.
    private void DetectNewlyLaunchedClients()
    {
        var assignedWindows = Windows.Where(IsWindowAssigned).ToList();
        var currentAssigned = assignedWindows
            .Select(w => w.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // First refresh only seeds the baseline — clients already open at launch are left in place
        // (use "Apply a layout profile on startup" to position those). Auto-apply only fires for
        // clients that appear *after* EveDeck is running.
        var firstRefresh = !_clientBaselineInitialized;
        _clientBaselineInitialized = true;

        var newlyAppeared = currentAssigned.Where(t => !_knownAssignedTitles.Contains(t)).ToList();

        // Replace the baseline so a client must actually disappear and return to re-trigger.
        _knownAssignedTitles.Clear();
        foreach (var title in currentAssigned) _knownAssignedTitles.Add(title);

        // Detect a character switch on a window that never closed (same PID, title's character changed).
        var characterSwitched = false;
        var currentCharacterByPid = new Dictionary<int, string>();
        foreach (var window in assignedWindows)
        {
            if (window.ProcessId <= 0) continue;
            var character = CharacterNameFromTitle(window.Title);
            currentCharacterByPid[window.ProcessId] = character;

            if (!firstRefresh
                && _lastKnownCharacterByPid.TryGetValue(window.ProcessId, out var previousCharacter)
                && !previousCharacter.Equals(character, StringComparison.OrdinalIgnoreCase))
            {
                characterSwitched = true;
                Log.Info($"Detected character switch on PID {window.ProcessId}: '{previousCharacter}' -> '{character}'.");

                // Rename the matched entry to the new character ONLY when that character isn't already
                // assigned to this seat. On character-SET seats the switched-to alt has its own entry, so
                // renaming this one would duplicate it and drop the alt that just logged off (the exact
                // corruption that hit the Overoth/Andr3sa seats). Single-window seats (new char not in the
                // set) still follow a permanent relog. Display labels update live via UpdateLiveSeatCharacters.
                var slot = Assignments.FirstOrDefault(a => a.AssignedWindows.Any(e => e.LastProcessId == window.ProcessId));
                var entry = slot?.AssignedWindows.FirstOrDefault(e => e.LastProcessId == window.ProcessId);
                if (entry is not null && slot is not null
                    && !slot.AssignedWindows.Any(e => e != entry && CharacterNameFromTitle(e.Title).Equals(character, StringComparison.OrdinalIgnoreCase)))
                    entry.Title = window.Title;
                if (slot is not null) TryAutoLabelSlot(slot, window);
            }
        }
        _lastKnownCharacterByPid.Clear();
        foreach (var (pid, character) in currentCharacterByPid) _lastKnownCharacterByPid[pid] = character;

        // Note: we deliberately do NOT skip auto-apply on the first refresh anymore. If a client is
        // already logged into a character by the time EveDeck's very first scan runs (common when the
        // user mass-launches clients right around EveDeck starting), it used to be silently absorbed
        // into the baseline and left un-positioned until a manual Refresh. Reusing the same debounce
        // path here means "first launch" and "later launch" behave identically.
        if (!_settings.AutoApplyOnClientLaunch) return;
        if ((newlyAppeared.Count == 0 && !characterSwitched) || _applyInProgress || SelectedProfile is null) return;

        // (Re)start the debounce; further launches/switches within the window push the apply back.
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
        // Master is identified by SlotNumber / IsMaster, never by list position, so leave the seat
        // where the user put it. Moving it to index 0 here reshuffled the manual seat order every
        // time a master was set or a tile was centered -- the "seat order won't stick" bug.
        OnPropertyChanged(nameof(MasterSlotNumber));
        OnPropertyChanged(nameof(MasterSeatSummary));
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
            RaiseWindowAssignmentDependents();
        };
    }

    // Detected Windows' filtered count + its auto-collapse state depend on which windows are
    // assigned, not just how many are detected -- raise these wherever an assignment/window-list
    // change could flip AllWindowsAssigned (Auto-Assign All, drag-drop, Clear, a client relaunching).
    private void RaiseWindowAssignmentDependents()
    {
        OnPropertyChanged(nameof(UnassignedWindowCount));
        OnPropertyChanged(nameof(AllWindowsAssigned));
    }

    private void OnAssignmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SlotAssignment.Label))
        {
            ScheduleAutoSave();
            RaiseIdentityDependents();
            RebuildMiniMap();
            return;
        }

        // Per-seat label placement/alias and zoom anchor are all baked into the overlay surfaces when
        // they are built, so changing one has no visible effect until the overlay is rebuilt. Cheap
        // enough to do on edit (these are hand-edited in the Clients tab, not changed in a loop).
        if (e.PropertyName is nameof(SlotAssignment.LabelAnchor)
                           or nameof(SlotAssignment.LabelAnchorMaster)
                           or nameof(SlotAssignment.LabelAlias)
                           or nameof(SlotAssignment.ZoomAnchor)
                           or nameof(SlotAssignment.ZoomFactor))
        {
            ScheduleAutoSave();
            if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    private void OnAssignedWindowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        WindowsView.Refresh();
        RaiseIdentityDependents();
        RaiseWindowAssignmentDependents();
        RebuildMiniMap();
    }
}
