using EveWindowCommander.Models;

namespace EveWindowCommander.Services;

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

        for (var i = 1; i <= 5; i++)
        {
            bindings.Add(new HotkeyBinding { ActionId = $"MoveActiveToSlot{i}", DisplayName = $"Move active EVE window to slot {i}", Modifiers = ModControl | ModAlt | ModShift, VirtualKey = (uint)('0' + i), GestureText = $"Ctrl+Alt+Shift+{i}" });
            bindings.Add(new HotkeyBinding { ActionId = $"SwapActiveWithSlot{i}", DisplayName = $"Swap active EVE window with slot {i}", Modifiers = ModControl | ModAlt | ModShift, VirtualKey = (uint)(0x70 + i - 1), GestureText = $"Ctrl+Alt+Shift+F{i}" });
        }

        // 3d — Assign the current foreground EVE window to the next empty slot.
        bindings.Add(new HotkeyBinding { ActionId = "QuickAssignActive", DisplayName = "Assign active window to next empty slot", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // 3b — Undo the last layout apply (restores windows to pre-apply positions).
        bindings.Add(new HotkeyBinding { ActionId = "UndoLastApply", DisplayName = "Undo last layout apply", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        // Swap the focused EVE window's slot with the designated master slot.
        bindings.Add(new HotkeyBinding { ActionId = "SwapFocusedWithMaster", DisplayName = "Swap focused client into master slot", Modifiers = ModControl | ModAlt, VirtualKey = (uint)'M', GestureText = "Ctrl+Alt+M" });

        // Per-slot swap-into-master hotkeys: rotate slot N's character into the master slot and
        // the master's character down into corner N. This is the core fast-switch mechanic, so it
        // ships with default gestures (Ctrl+Shift+1..4). Rebind in the Hotkeys tab if they clash.
        for (var i = 1; i <= 4; i++)
            bindings.Add(new HotkeyBinding { ActionId = $"SwapSlotWithMaster{i}", DisplayName = $"Swap slot {i} into master slot", Modifiers = ModControl | ModShift, VirtualKey = (uint)('0' + i), GestureText = $"Ctrl+Shift+{i}" });

        // Direction-based focus hotkeys: each maps to the slot at that screen position in the active
        // profile, then brings it to the centre (overlay mode) or foreground (flat mode). Ship unbound
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
            bindings.Add(new HotkeyBinding { ActionId = $"SwitchToCharacter{i}", DisplayName = $"Switch to character {i} (pick target →)", Modifiers = 0, VirtualKey = 0, GestureText = "" });

        return bindings;
    }
}
