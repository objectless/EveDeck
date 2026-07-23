using EveDeck.Models;

namespace EveDeck.Services;

public static class HotkeyDefaults
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;

    public static IReadOnlyList<HotkeyBinding> Create()
    {
        var bindings = new List<HotkeyBinding>();
        // Focus the five real client slots (the user runs at most 5 concurrent characters).
        for (var i = 1; i <= 5; i++)
        {
            bindings.Add(new HotkeyBinding
            {
                ActionId = $"FocusSlot{i}",
                DisplayName = $"Focus slot {i}",
                Modifiers = ModControl | ModAlt,
                VirtualKey = (uint)('0' + i),
                GestureText = $"Ctrl+Alt+{i}"
            });
        }

        bindings.Add(new HotkeyBinding { ActionId = "ApplyLayout", DisplayName = "Apply active layout", Modifiers = ModControl | ModAlt, VirtualKey = (uint)'A', GestureText = "Ctrl+Alt+A" });

        // Ship unbound (like the direction/character-switch hotkeys below) so the default gesture set
        // stays small and collision-free out of the box; rebind in the Hotkeys tab if wanted.
        for (var i = 1; i <= 5; i++)
        {
            bindings.Add(new HotkeyBinding { ActionId = $"MoveActiveToSlot{i}", DisplayName = $"Move active EVE window to slot {i}", Modifiers = 0, VirtualKey = 0, GestureText = "" });
            bindings.Add(new HotkeyBinding { ActionId = $"SwapActiveWithSlot{i}", DisplayName = $"Swap active EVE window with slot {i}", Modifiers = 0, VirtualKey = 0, GestureText = "" });
        }

        // 3d — Assign the current foreground EVE window to the next empty slot.
        bindings.Add(new HotkeyBinding { ActionId = "QuickAssignActive", DisplayName = "Assign active window to next empty slot", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // 3b — Undo the last layout apply (restores windows to pre-apply positions).
        bindings.Add(new HotkeyBinding { ActionId = "UndoLastApply", DisplayName = "Undo last layout apply", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // Swap the focused EVE window's slot with the designated master slot. Ships unbound; rebind
        // in the Hotkeys tab if wanted.
        bindings.Add(new HotkeyBinding { ActionId = "SwapFocusedWithMaster", DisplayName = "Swap focused client into master slot", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // Per-slot swap-into-master hotkeys: rotate slot N's character into the master slot and
        // the master's character down into corner N. Core fast-switch mechanic, but ships unbound
        // like the rest of the Move/Swap group to keep the out-of-box gesture set small.
        for (var i = 1; i <= 4; i++)
            bindings.Add(new HotkeyBinding { ActionId = $"SwapSlotWithMaster{i}", DisplayName = $"Swap slot {i} into master slot", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // Direction-based focus hotkeys: each maps to the slot at that screen position in the active
        // profile, then brings it to the center (overlay mode) or foreground (flat mode). Ship unbound
        // so the user can assign them to Tartarus F-keys or any macro device.
        var directions = new (string id, string label)[]
        {
            ("TL", "top-left"),
            ("TC", "top-center"),
            ("TR", "top-right"),
            ("ML", "left"),
            ("C",  "center"),
            ("MR", "right"),
            ("BL", "bottom-left"),
            ("BC", "bottom-center"),
            ("BR", "bottom-right"),
        };
        foreach (var (id, label) in directions)
            bindings.Add(new HotkeyBinding { ActionId = $"FocusDirection{id}", DisplayName = $"Focus {label} slot", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // Character-following switch hotkeys. Each binds to a specific character (by name) chosen in
        // the Hotkeys tab; pressing it resolves where that character currently sits and either swaps
        // it into the master slot (corner-overlay mode) or focuses it (flat layouts). Unlike the
        // slot-based swaps above, these follow the character across master swaps — so e.g. F21 always
        // brings a given character forward no matter which corner they've rotated into. Ship unbound
        // (assign each binding a target character in the Hotkeys tab).
        for (var i = 1; i <= 5; i++)
            bindings.Add(new HotkeyBinding { ActionId = $"SwitchToCharacter{i}", DisplayName = $"Switch to character {i} (pick target ->)", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        bindings.Add(new HotkeyBinding { ActionId = "ToggleTopmost", DisplayName = "Toggle always-on-top for focused EVE window", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // Minimize every EVE client at once (except seats marked "never minimize") — panic button /
        // boss key. Window-management only; ships unbound.
        bindings.Add(new HotkeyBinding { ActionId = "MinimizeAllClients", DisplayName = "Minimize all EVE clients (skips protected seats)", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        for (var i = 1; i <= 4; i++)
            bindings.Add(new HotkeyBinding { ActionId = $"SwitchCharacterSet{i}", DisplayName = $"Switch to character set {i}", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // Per-group focus cycling: cycle forward/back through only the seats in swap group N of the
        // active profile (group 1 = the whole roster when the profile has no explicit groups). Window
        // focus only -- ships unbound.
        for (var i = 1; i <= 4; i++)
        {
            bindings.Add(new HotkeyBinding { ActionId = $"CycleGroupNext{i}", DisplayName = $"Cycle group {i} forward", Modifiers = 0, VirtualKey = 0, GestureText = "" });
            bindings.Add(new HotkeyBinding { ActionId = $"CycleGroupPrevious{i}", DisplayName = $"Cycle group {i} backward", Modifiers = 0, VirtualKey = 0, GestureText = "" });
        }

        // Jump focus back to the last non-EVE window you were using (e.g. a browser/spreadsheet) --
        // pure window focus, ships unbound.
        bindings.Add(new HotkeyBinding { ActionId = "FocusPreviousApp", DisplayName = "Focus last non-EVE window", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // Panic pause: suspend/resume every EveDeck hotkey at once (this toggle keeps working while
        // suspended). Ships unbound.
        bindings.Add(new HotkeyBinding { ActionId = "ToggleHotkeysSuspended", DisplayName = "Suspend / resume all hotkeys", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // Manual kick for a stale/stuck preview: force every corner tile to unregister and
        // re-register its DWM thumbnail (TileSurfaceWindow.RefreshAllSources). That re-registration is
        // a brief visible blink, so this stays a deliberate, user-triggered action -- never something
        // fired automatically on a timer. Ships unbound.
        bindings.Add(new HotkeyBinding { ActionId = "ForceRefreshPreviews", DisplayName = "Force refresh previews", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // Suspend/resume every corner preview at once -- hides the overlay surfaces so DWM stops
        // compositing the live thumbnails (a GPU/clutter breather) without tearing the overlay down,
        // so resuming is instant. Also on the tray menu. Ships unbound.
        bindings.Add(new HotkeyBinding { ActionId = "TogglePreviewsSuspended", DisplayName = "Suspend / resume all previews", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        return bindings;
    }
}
