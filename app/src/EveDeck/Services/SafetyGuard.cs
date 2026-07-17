namespace EveDeck.Services;

public static class SafetyGuard
{
    private static readonly HashSet<string> AllowedActionPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FocusSlot",
        "CycleNext",
        "CyclePrevious",
        "CycleGroupNext",
        "CycleGroupPrevious",
        "FocusPreviousApp",
        "ToggleHotkeysSuspended",
        "ApplyLayout",
        "ToggleBorderless",
        "MoveActiveToSlot",
        "SwapActiveWithSlot",
        "RestoreActiveStyle",
        "QuickAssignActive",
        "UndoLastApply",
        "SwapFocusedWithMaster",
        "SwapSlotWithMaster",
        "SwitchToCharacter",
        "FocusDirection",
        "ToggleTopmost",
        "SwitchCharacterSet",
        "MinimizeAllClients"
    };

    public static bool AllowsHotkeyAction(string actionId)
        => AllowedActionPrefixes.Any(prefix => actionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static void ThrowIfInputBroadcastAction(string actionId)
    {
        var blockedWords = new[] { "SendKey", "KeyForward", "Click", "Broadcast", "Multiplex", "Automate", "Input" };
        if (!AllowsHotkeyAction(actionId) || blockedWords.Any(word => actionId.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("EveDeck blocks keyboard/mouse input forwarding and broadcasting actions.");
        }
    }
}
