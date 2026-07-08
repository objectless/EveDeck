using Xunit;
using EveDeck.Utilities;

namespace EveDeck.Tests;

public class EveThemeStringTests
{
    [Fact]
    public void TryParse_FourColorString_ExtractsPrimaryAndAccentUppercase()
    {
        var ok = EveThemeString.TryParse("#36C65E,#2CBA58,#000000,#404880", out var primary, out var accent);

        Assert.True(ok);
        Assert.Equal("#36C65E", primary);
        Assert.Equal("#2CBA58", accent);
    }

    [Fact]
    public void TryParse_LowercaseAndWhitespace_NormalizesToUppercaseNoSpaces()
    {
        var ok = EveThemeString.TryParse(" #36c65e , #2cba58 ", out var primary, out var accent);

        Assert.True(ok);
        Assert.Equal("#36C65E", primary);
        Assert.Equal("#2CBA58", accent);
    }

    [Fact]
    public void TryParse_MissingHashPrefix_StillParses()
    {
        var ok = EveThemeString.TryParse("36C65E,2CBA58", out var primary, out var accent);

        Assert.True(ok);
        Assert.Equal("#36C65E", primary);
        Assert.Equal("#2CBA58", accent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a color")]
    [InlineData("#36C65E")]                    // only one color
    [InlineData("#ZZZZZZ,#2CBA58")]             // invalid hex digits
    [InlineData("#36C6,#2CBA58")]               // too short
    public void TryParse_InvalidInput_ReturnsFalse(string raw)
    {
        var ok = EveThemeString.TryParse(raw, out var primary, out var accent);

        Assert.False(ok);
        Assert.Equal("", primary);
        Assert.Equal("", accent);
    }

    [Fact]
    public void TryParse_NullInput_ReturnsFalse()
    {
        var ok = EveThemeString.TryParse(null, out _, out _);
        Assert.False(ok);
    }
}
