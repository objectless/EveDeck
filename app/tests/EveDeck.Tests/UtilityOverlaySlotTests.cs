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
        Assert.Equal(100, slot.OpacityPercent);
        Assert.Equal(100, slot.ScalePercent);
    }
}
