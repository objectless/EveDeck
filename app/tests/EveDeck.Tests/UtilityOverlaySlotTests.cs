using Xunit;
using EveDeck.Models;

namespace EveDeck.Tests;

public class UtilityOverlaySlotTests
{
    [Fact]
    public void Defaults_AreDisabledUnlockedWithSensibleSize()
    {
        var slot = new UtilityOverlaySlot();

        Assert.False(slot.Enabled);
        Assert.False(slot.Locked);
        Assert.Equal(0, slot.X);
        Assert.Equal(0, slot.Y);
        Assert.Equal(420, slot.Width);
        Assert.Equal(560, slot.Height);
        Assert.Equal(100, slot.OpacityPercent);
        Assert.Null(slot.OriginalRect);
        Assert.Equal(0, slot.OriginalStyle);
        Assert.Equal(0, slot.OriginalExStyle);
    }
}
