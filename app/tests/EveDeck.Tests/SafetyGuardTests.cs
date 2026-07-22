using Xunit;
using EveDeck.Services;
using EveDeck.Models;

namespace EveDeck.Tests;

public class SafetyGuardTests
{
    [Fact]
    public void AllDefaultHotkeyActions_AreAllowed()
    {
        var defaults = HotkeyDefaults.Create();

        foreach (var binding in defaults)
        {
            Assert.True(SafetyGuard.AllowsHotkeyAction(binding.ActionId),
                $"Default action '{binding.ActionId}' should be allowed");
        }
    }

    [Theory]
    [InlineData("SendKeys")]
    [InlineData("BroadcastAll")]
    [InlineData("InputForward")]
    [InlineData("")]
    [InlineData("UnknownAction")]
    [InlineData("CustomBlock")]
    public void UnknownActions_AreRejected(string actionId)
    {
        Assert.False(SafetyGuard.AllowsHotkeyAction(actionId),
            $"Action '{actionId}' should not be allowed");
    }

    [Theory]
    [InlineData("FocusSlotBroadcast")]
    [InlineData("ApplyLayoutSendKey")]
    public void ThrowIfInputBroadcastAction_ThrowsOnBlockedWordEvenWithAllowedPrefix(string actionId)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            SafetyGuard.ThrowIfInputBroadcastAction(actionId);
        });

        Assert.Contains("blocks keyboard/mouse input", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("FocusSlot1")]
    [InlineData("FocusSlot2")]
    [InlineData("ApplyLayout")]
    [InlineData("SwapFocusedWithMaster")]
    [InlineData("SwitchToCharacter1")]
    public void ThrowIfInputBroadcastAction_PassesKnownActions(string actionId)
    {
        SafetyGuard.ThrowIfInputBroadcastAction(actionId);
    }

    [Theory]
    [InlineData("focusslot1")]
    [InlineData("FOCUSSLOT1")]
    [InlineData("FocusSlot1")]
    public void AllowsHotkeyAction_IsCaseInsensitive(string actionId)
    {
        Assert.True(SafetyGuard.AllowsHotkeyAction(actionId),
            $"Action '{actionId}' should be allowed (case-insensitive)");
    }

    // ── Whole-window-preview rule ──────────────────────────────────────────────
    // A preview must always show the ENTIRE EVE client window. Cropping it (rcSource /
    // DWM_TNP_RECTSOURCE) is against the EULA -- see COMPLIANCE.md and AGENTS.md.

    [Fact]
    public void ThrowIfSourceCrop_RejectsRectSourceFlag()
    {
        Assert.Throws<InvalidOperationException>(
            () => SafetyGuard.ThrowIfSourceCrop(SafetyGuard.DwmTnpRectSourceFlag));
    }

    [Fact]
    public void ThrowIfSourceCrop_RejectsRectSourceCombinedWithOtherFlags()
    {
        // The realistic mistake is ORing rcSource in alongside the flags we legitimately use, not
        // passing it alone -- make sure the mask check catches that rather than an equality check.
        const int destination = 0x00000001, visible = 0x00000008, opacity = 0x00000004;
        var flags = destination | visible | opacity | SafetyGuard.DwmTnpRectSourceFlag;

        Assert.Throws<InvalidOperationException>(() => SafetyGuard.ThrowIfSourceCrop(flags));
    }

    [Fact]
    public void ThrowIfSourceCrop_AllowsWholeWindowFlags()
    {
        // Destination rect + visible + opacity is exactly what the tile surface uses. Scaling the
        // WHOLE window is the supported way to make a preview readable and must keep working.
        const int destination = 0x00000001, visible = 0x00000008, opacity = 0x00000004;

        SafetyGuard.ThrowIfSourceCrop(destination | visible | opacity);
    }

    [Theory]
    [InlineData("CropPreview")]
    [InlineData("PreviewSlice")]
    [InlineData("HudSourceRect")]
    [InlineData("PartialWindowPreview")]
    [InlineData("CapacitorRegionOnly")]
    public void ThrowIfPreviewCropFeature_RejectsCroppingFeatureNames(string featureName)
    {
        Assert.Throws<InvalidOperationException>(
            () => SafetyGuard.ThrowIfPreviewCropFeature(featureName));
    }

    [Theory]
    [InlineData("HoverZoom")]
    [InlineData("TileSize")]
    [InlineData("PreviewOpacity")]
    public void ThrowIfPreviewCropFeature_AllowsWholeWindowFeatureNames(string featureName)
    {
        SafetyGuard.ThrowIfPreviewCropFeature(featureName);
    }
}
