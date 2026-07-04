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
}
