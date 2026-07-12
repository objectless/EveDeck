using Xunit;
using EveDeck.Services;

namespace EveDeck.Tests;

public class FactoryLoadCalculatorTests
{
    private static FactoryLoadCalculator.Input In(string name, long qty) => new(name, qty);

    [Fact]
    public void Compute_SplitsLowestInputEvenlyWithRemainder()
    {
        // The eve-pi worked example: Precious Metals lowest at 90,760 across 16 factories.
        var inputs = new[]
        {
            In("Biofuels", 120000),
            In("Precious Metals", 90760),
            In("Water", 95000),
        };

        var r = FactoryLoadCalculator.Compute(inputs, factoryCount: 16, burnPerHour: 240);

        Assert.Equal("Precious Metals", r.LimitingInput);
        Assert.Equal(90760, r.LimitingAvailable);
        Assert.Equal(5672, r.PerFactoryLow);       // 90760 / 16 = 5672 r8
        Assert.Equal(8, r.FactoriesGettingExtra);  // remainder 8 factories get +1
        Assert.Equal(5673, r.ExtraQuantity);
        Assert.Equal(5672, r.BaseQuantity);
    }

    [Fact]
    public void Compute_RemainderShareIsDistributedThenBaseShare()
    {
        var inputs = new[] { In("A", 100) };
        var r = FactoryLoadCalculator.Compute(inputs, factoryCount: 3, burnPerHour: 10);

        // 100 / 3 = 33 r1 → one factory gets 34, two get 33.
        Assert.Equal(33, r.PerFactoryLow);
        Assert.Equal(1, r.FactoriesGettingExtra);
        Assert.Equal(3, r.Shares.Count);
        Assert.Equal(34, r.Shares[0].PerInputQuantity);
        Assert.Equal(33, r.Shares[1].PerInputQuantity);
        Assert.Equal(33, r.Shares[2].PerInputQuantity);
        Assert.Equal(100, r.Shares.Sum(s => s.PerInputQuantity)); // conserves the full lowest stock
    }

    [Fact]
    public void Compute_RuntimeIsLowTierDividedByBurnRate()
    {
        var inputs = new[] { In("A", 3840), In("B", 4000) };
        var r = FactoryLoadCalculator.Compute(inputs, factoryCount: 16, burnPerHour: 240);

        // Lowest 3840 / 16 = 240 per factory; at 240/hr that is exactly 1 hour.
        Assert.Equal(240, r.PerFactoryLow);
        Assert.Equal(0, r.FactoriesGettingExtra);
        Assert.Equal(1.0, r.RuntimeHours, 3);
    }

    [Fact]
    public void Compute_ZeroStockGivesZeroSplitNotCrash()
    {
        var inputs = new[] { In("A", 0), In("B", 500) };
        var r = FactoryLoadCalculator.Compute(inputs, factoryCount: 16, burnPerHour: 240);

        Assert.Equal("A", r.LimitingInput);
        Assert.Equal(0, r.PerFactoryLow);
        Assert.Equal(0, r.FactoriesGettingExtra);
        Assert.Equal(0.0, r.RuntimeHours);
    }

    [Theory]
    [InlineData(0, 240)]   // zero factories
    [InlineData(16, 0)]    // zero burn rate
    public void Compute_RejectsInvalidArguments(int factories, double burn)
    {
        var inputs = new[] { In("A", 100) };
        Assert.Throws<ArgumentException>(() => FactoryLoadCalculator.Compute(inputs, factories, burn));
    }

    [Fact]
    public void Compute_RejectsEmptyInputs()
        => Assert.Throws<ArgumentException>(
            () => FactoryLoadCalculator.Compute(Array.Empty<FactoryLoadCalculator.Input>(), 16, 240));
}
