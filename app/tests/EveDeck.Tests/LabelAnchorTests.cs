using Xunit;
using EveDeck.Views;

namespace EveDeck.Tests;

public class LabelAnchorTests
{
    [Theory]
    [InlineData("TopLeft", "Left", "Top")]
    [InlineData("TopCenter", "Center", "Top")]
    [InlineData("TopRight", "Right", "Top")]
    [InlineData("MiddleLeft", "Left", "Middle")]
    [InlineData("Center", "Center", "Middle")]
    [InlineData("MiddleRight", "Right", "Middle")]
    [InlineData("BottomLeft", "Left", "Bottom")]
    [InlineData("BottomCenter", "Center", "Bottom")]
    [InlineData("BottomRight", "Right", "Bottom")]
    public void ParseAnchor_MapsAllNinePositions(string value, string x, string y)
    {
        var anchor = LabelSurfaceWindow.ParseAnchor(value);

        Assert.Equal(x, anchor.X.ToString());
        Assert.Equal(y, anchor.Y.ToString());
    }

    [Theory]
    [InlineData("topleft")]
    [InlineData("TOPLEFT")]
    [InlineData("  TopLeft  ")]
    public void ParseAnchor_IsCaseAndWhitespaceInsensitive(string value)
    {
        var anchor = LabelSurfaceWindow.ParseAnchor(value);

        Assert.Equal("Left", anchor.X.ToString());
        Assert.Equal("Top", anchor.Y.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Nonsense")]
    // A settings.json written before this setting existed deserialises the string as null/empty, and
    // a hand-edited file can hold anything. Both must land on the documented default rather than
    // throwing or leaving labels unplaced.
    public void ParseAnchor_FallsBackToCenter(string? value)
    {
        var anchor = LabelSurfaceWindow.ParseAnchor(value);

        Assert.Equal("Center", anchor.X.ToString());
        Assert.Equal("Middle", anchor.Y.ToString());
    }
}
