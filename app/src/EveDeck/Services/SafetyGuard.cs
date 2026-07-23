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
        "MinimizeAllClients",
        "ForceRefreshPreviews",
        "TogglePreviewsSuspended"
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

    // ── Whole-window-preview rule ──────────────────────────────────────────────
    //
    // A preview must always show the ENTIRE EVE client window. Cropping or slicing it -- showing
    // only the capacitor, only the HUD, only local chat -- is against the EVE EULA: it turns a
    // passive thumbnail into a purpose-built telemetry panel assembled from pieces of the client.
    // EVE-APM Preview draws the same line explicitly, listing "display portions of your eve client"
    // alongside input broadcasting in what it will never do.
    //
    // Mechanically this means DWM_THUMBNAIL_PROPERTIES.rcSource must never be used, and
    // DWM_TNP_RECTSOURCE must never appear in dwFlags -- rcSource is precisely the "show me only
    // this part of the source window" knob. Scaling the WHOLE window (rcDestination, hover zoom) is
    // fine and is the supported way to make a preview more readable.
    //
    // Call ThrowIfSourceCrop from any code path that builds thumbnail properties.
    public const int DwmTnpRectSourceFlag = 0x00000002; // DWM_TNP_RECTSOURCE

    public static void ThrowIfSourceCrop(int dwFlags)
    {
        if ((dwFlags & DwmTnpRectSourceFlag) != 0)
        {
            throw new InvalidOperationException(
                "EveDeck previews must show the whole EVE client window. Cropping the source "
                + "(DWM_TNP_RECTSOURCE / rcSource) is against the EVE EULA -- scale the destination "
                + "rect or use hover zoom instead.");
        }
    }

    // Same rule expressed for feature/setting names, so a future "CropPreview" or "PreviewSlice"
    // option trips the guard at the point someone tries to wire it up rather than in review.
    public static void ThrowIfPreviewCropFeature(string featureName)
    {
        var blockedWords = new[] { "Crop", "Slice", "SourceRect", "PartialWindow", "RegionOnly" };
        if (blockedWords.Any(word => featureName.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"EveDeck blocks preview cropping/slicing features ('{featureName}'). Previews must "
                + "show the whole EVE client window -- see the whole-window-preview rule in SafetyGuard.");
        }
    }
}
