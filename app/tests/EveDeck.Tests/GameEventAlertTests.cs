using EveDeck.Models;
using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

public class GameEventAlertTests
{
    [Fact]
    public void ParseListener_FindsCharacterInGamelogHeader()
    {
        var header = new[]
        {
            "------------------------------------------------------------",
            "  Gamelog",
            "  Listener: Aria Jenneth",
            "  Session Started: 2026.07.10 04:00:00",
            "------------------------------------------------------------"
        };
        Assert.Equal("Aria Jenneth", GameLogWatcherService.ParseListener(header));
    }

    [Fact]
    public void ParseListener_FindsCharacterInChatlogHeader_PaddedFormat()
    {
        var header = new[]
        {
            "---------------------------------------------------------------",
            "  Channel ID:      local",
            "  Channel Name:    Local",
            "  Listener:        Scout Alt",
            "  Session started: 2026.07.10 03:00:00",
            "---------------------------------------------------------------"
        };
        Assert.Equal("Scout Alt", GameLogWatcherService.ParseListener(header));
    }

    [Fact]
    public void ParseListener_ReturnsNullWhenAbsent()
    {
        Assert.Null(GameLogWatcherService.ParseListener(new[] { "no header here", "[ 2026.07.10 ] (combat) hit" }));
    }

    [Fact]
    public void Defaults_AreEnabledAndHavePatterns()
    {
        var defaults = GameEventRule.Defaults().ToList();
        Assert.NotEmpty(defaults);
        Assert.All(defaults, r =>
        {
            Assert.True(r.Enabled);
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.False(string.IsNullOrWhiteSpace(r.Pattern));
        });
        // Combat must suppress-when-focused by default (you can see the active client already);
        // fleet invites must NOT (they matter regardless of which window is active).
        Assert.True(defaults.First(r => r.Name == "Combat").SuppressWhenFocused);
        Assert.False(defaults.First(r => r.Name == "Fleet invite").SuppressWhenFocused);
    }

    [Fact]
    public void FreshAppSettings_SeedDefaultGameEventRules()
    {
        var settings = new AppSettings();
        Assert.NotEmpty(settings.GameEventRules);
    }

    [Theory]
    [InlineData("MinimizeAllClients")]
    [InlineData("FocusSlot3")]
    [InlineData("SwitchToCharacter")]
    [InlineData("ApplyLayout")]
    [InlineData("SwitchCharacterSet2")]
    public void SafetyGuard_AllowsProtocolBackedActions(string actionId)
    {
        Assert.True(SafetyGuard.AllowsHotkeyAction(actionId));
        SafetyGuard.ThrowIfInputBroadcastAction(actionId); // must not throw
    }

    [Theory]
    [InlineData("SendKeyToAll")]
    [InlineData("BroadcastClick")]
    [InlineData("InputForward")]
    public void SafetyGuard_StillBlocksInputActions(string actionId)
    {
        Assert.Throws<InvalidOperationException>(() => SafetyGuard.ThrowIfInputBroadcastAction(actionId));
    }
}
