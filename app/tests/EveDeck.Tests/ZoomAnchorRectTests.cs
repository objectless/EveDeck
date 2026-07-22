using System.Drawing;
using Xunit;
using EveDeck.Views;

namespace EveDeck.Tests;

// Pure geometry tests for TileSurfaceWindow.ComputeZoomRect -- no Form is constructed, this only
// exercises the anchor-pinning math pulled out of ZoomTile.
public class ZoomAnchorRectTests
{
    private static readonly Rectangle Tile = new(100, 200, 80, 60);

    [Fact]
    public void Center_GrowsEvenlyInAllDirections_MatchesPreAnchorBehavior()
    {
        // This must stay byte-identical to the original always-grow-from-center formula
        // (rect.Left + rect.Width/2 - w/2), since Center/Middle is the pre-existing default.
        var anchor = new LabelAnchor(LabelAnchorX.Center, LabelAnchorY.Middle);
        var zoom = TileSurfaceWindow.ComputeZoomRect(Tile, 160, 120, anchor);

        Assert.Equal(Tile.Left + Tile.Width / 2 - 160 / 2, zoom.X);
        Assert.Equal(Tile.Top + Tile.Height / 2 - 120 / 2, zoom.Y);
        Assert.Equal(160, zoom.Width);
        Assert.Equal(120, zoom.Height);
    }

    [Fact]
    public void TopLeft_PinsTopLeftCorner_GrowsDownAndRight()
    {
        var anchor = new LabelAnchor(LabelAnchorX.Left, LabelAnchorY.Top);
        var zoom = TileSurfaceWindow.ComputeZoomRect(Tile, 160, 120, anchor);

        Assert.Equal(Tile.Left, zoom.Left);
        Assert.Equal(Tile.Top, zoom.Top);
    }

    [Fact]
    public void BottomRight_PinsBottomRightCorner_GrowsUpAndLeft()
    {
        var anchor = new LabelAnchor(LabelAnchorX.Right, LabelAnchorY.Bottom);
        var zoom = TileSurfaceWindow.ComputeZoomRect(Tile, 160, 120, anchor);

        Assert.Equal(Tile.Right, zoom.Right);
        Assert.Equal(Tile.Bottom, zoom.Bottom);
    }

    [Fact]
    public void MiddleLeft_PinsLeftEdge_GrowsVerticallyEvenly()
    {
        var anchor = new LabelAnchor(LabelAnchorX.Left, LabelAnchorY.Middle);
        var zoom = TileSurfaceWindow.ComputeZoomRect(Tile, 160, 120, anchor);

        Assert.Equal(Tile.Left, zoom.Left);
        Assert.Equal(Tile.Top + Tile.Height / 2 - 120 / 2, zoom.Top);
    }

    [Fact]
    public void BottomCenter_PinsBottomEdge_GrowsHorizontallyEvenly()
    {
        var anchor = new LabelAnchor(LabelAnchorX.Center, LabelAnchorY.Bottom);
        var zoom = TileSurfaceWindow.ComputeZoomRect(Tile, 160, 120, anchor);

        Assert.Equal(Tile.Left + Tile.Width / 2 - 160 / 2, zoom.Left);
        Assert.Equal(Tile.Bottom, zoom.Bottom);
    }

    // Routed through ParseAnchor (string in) rather than the internal LabelAnchorX/Y enums directly --
    // a public [Theory] method can't take an internal enum as a parameter (CS0051).
    [Theory]
    [InlineData("TopLeft")]
    [InlineData("TopCenter")]
    [InlineData("TopRight")]
    [InlineData("MiddleLeft")]
    [InlineData("Center")]
    [InlineData("MiddleRight")]
    [InlineData("BottomLeft")]
    [InlineData("BottomCenter")]
    [InlineData("BottomRight")]
    public void AllNineAnchors_ProduceRequestedSize(string anchorName)
    {
        var anchor = LabelSurfaceWindow.ParseAnchor(anchorName);
        var zoom = TileSurfaceWindow.ComputeZoomRect(Tile, 160, 120, anchor);

        Assert.Equal(160, zoom.Width);
        Assert.Equal(120, zoom.Height);
    }
}
