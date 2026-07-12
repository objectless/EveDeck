using Xunit;
using EveDeck.Models;

namespace EveDeck.Tests;

public class PiExtractorTests
{
    private static PiExtractor WithExpiry(DateTimeOffset? expiry) => new()
    {
        ProductTypeId = 2309,
        ProductName = "Base Metals",
        ExpiryTime = expiry,
    };

    [Fact]
    public void RefreshCountdown_FutureBeyondThreshold_IsRunning_NoAlert()
    {
        var now = DateTimeOffset.UtcNow;
        var ext = WithExpiry(now.AddHours(20));

        var alert = ext.RefreshCountdown(now, alertHours: 6);

        Assert.False(alert);
        Assert.Equal(PiExtractorState.Running, ext.State);
        Assert.Contains("h", ext.CountdownText);
    }

    [Fact]
    public void RefreshCountdown_WithinThreshold_IsExpiringSoon_AndAlerts()
    {
        var now = DateTimeOffset.UtcNow;
        var ext = WithExpiry(now.AddHours(3));

        var alert = ext.RefreshCountdown(now, alertHours: 6);

        Assert.True(alert);
        Assert.Equal(PiExtractorState.ExpiringSoon, ext.State);
    }

    [Fact]
    public void RefreshCountdown_PastExpiry_IsExpired_AndAlerts()
    {
        var now = DateTimeOffset.UtcNow;
        var ext = WithExpiry(now.AddHours(-1));

        var alert = ext.RefreshCountdown(now, alertHours: 6);

        Assert.True(alert);
        Assert.Equal(PiExtractorState.Expired, ext.State);
        Assert.Equal("expired", ext.CountdownText);
    }

    [Fact]
    public void RefreshCountdown_NoExpiry_IsIdle_NoAlert()
    {
        var ext = WithExpiry(null);
        var alert = ext.RefreshCountdown(DateTimeOffset.UtcNow, alertHours: 6);

        Assert.False(alert);
        Assert.Equal(PiExtractorState.Idle, ext.State);
        Assert.Equal("idle", ext.CountdownText);
    }

    [Fact]
    public void DisplayName_WithRefinesInto_ShowsArrow()
    {
        var ext = new PiExtractor { ProductName = "Reactive Gas", RefinesInto = "Oxidizing Compound" };
        Assert.Equal("Reactive Gas → Oxidizing Compound", ext.DisplayName);
    }

    [Fact]
    public void DisplayName_WithoutRefinesInto_ShowsProductNameOnly()
    {
        var ext = new PiExtractor { ProductName = "Reactive Gas", RefinesInto = "" };
        Assert.Equal("Reactive Gas", ext.DisplayName);
    }

    [Fact]
    public void Storage_FillPercent_ClampsAndHandlesZeroCapacity()
    {
        Assert.Equal(50, new PiStorage { UsedVolume = 5000, Capacity = 10000 }.FillPercent, 3);
        Assert.Equal(100, new PiStorage { UsedVolume = 20000, Capacity = 10000 }.FillPercent, 3); // clamped
        Assert.Equal(0, new PiStorage { UsedVolume = 500, Capacity = 0 }.FillPercent, 3);          // no divide-by-zero
    }

    [Fact]
    public void Colony_WorstFillPercent_PicksFullestStorage()
    {
        var colony = new PiColony();
        colony.Storages.Add(new PiStorage { UsedVolume = 1000, Capacity = 10000 }); // 10%
        colony.Storages.Add(new PiStorage { UsedVolume = 9500, Capacity = 10000 }); // 95%
        Assert.Equal(95, colony.WorstFillPercent, 3);
    }

    [Fact]
    public void Colony_WorstFillPercent_ConsidersFactoriesToo()
    {
        var colony = new PiColony();
        colony.Storages.Add(new PiStorage { UsedVolume = 1000, Capacity = 10000 }); // 10%
        colony.Factories.Add(new PiFactory { UsedVolume = 9800, Capacity = 10000 }); // 98%, fuller than storage
        Assert.Equal(98, colony.WorstFillPercent, 3);
    }

    [Fact]
    public void Factory_RecipeText_ReflectsSchematicOrMissingOne()
    {
        var withRecipe = new PiFactory
        {
            PinName = "Gel-Matrix Biopaste Advanced Industry Facility",
            OutputName = "Gel-Matrix Biopaste",
            InputNames = new[] { "Biocells", "Oxides", "Superconductors" },
        };
        Assert.True(withRecipe.HasRecipe);
        Assert.Equal("producing Gel-Matrix Biopaste from Biocells, Oxides, Superconductors", withRecipe.RecipeText);

        var idle = new PiFactory { PinName = "Advanced Industry Facility" };
        Assert.False(idle.HasRecipe);
        Assert.Equal("no schematic set", idle.RecipeText);
    }
}
