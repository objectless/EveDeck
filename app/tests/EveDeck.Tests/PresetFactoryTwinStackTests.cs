using Xunit;
using EveDeck.Services;
using EveDeck.Models;

namespace EveDeck.Tests;

public class PresetFactoryTwinStackTests
{
    private static LayoutProfile Regenerate(int count, int width, int height)
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Twin Stack",
            TemplateWidth = width,
            TemplateHeight = height,
            TemplateCount = count,
        };
        PresetFactory.RegenerateFamilySlots(profile);
        return profile;
    }

    [Fact]
    public void TwinStack_5At2560x1440_TwoTilesPerEdgeMasterInBetween()
    {
        var profile = Regenerate(5, 2560, 1440);

        Assert.Equal(5, profile.Slots.Count);
        var slots = profile.Slots.OrderBy(s => s.SlotNumber).ToList();

        // perSide = 2, tileW = 2560/4 = 640, tile height = 1440/2 = 720.
        Assert.Equal("Left 1", slots[0].Label);
        Assert.Equal((0, 0, 640, 720), (slots[0].X, slots[0].Y, slots[0].Width, slots[0].Height));

        Assert.Equal("Left 2", slots[1].Label);
        Assert.Equal((0, 720, 640, 720), (slots[1].X, slots[1].Y, slots[1].Width, slots[1].Height));

        Assert.Equal("Right 1", slots[2].Label);
        Assert.Equal((1920, 0, 640, 720), (slots[2].X, slots[2].Y, slots[2].Width, slots[2].Height));

        Assert.Equal("Right 2", slots[3].Label);
        Assert.Equal((1920, 720, 640, 720), (slots[3].X, slots[3].Y, slots[3].Width, slots[3].Height));

        // Master fills the gap between the two columns, full height.
        Assert.Equal("Master", slots[4].Label);
        Assert.Equal((640, 0, 1280, 1440), (slots[4].X, slots[4].Y, slots[4].Width, slots[4].Height));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    public void TwinStack_AllCounts_ColumnsAreSymmetricAndMasterIsStrictlyLargest(int count)
    {
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440), (3200, 1800), (3840, 2160) })
        {
            var profile = Regenerate(count, w, h);

            Assert.Equal(count, profile.Slots.Count);
            Assert.Equal(Enumerable.Range(1, count), profile.Slots.Select(s => s.SlotNumber).OrderBy(n => n));

            var master = profile.Slots.Single(s => s.SlotNumber == count);
            Assert.Equal("Master", master.Label);
            long masterArea = (long)master.Width * master.Height;

            var left = profile.Slots.Where(s => s.Label.StartsWith("Left")).ToList();
            var right = profile.Slots.Where(s => s.Label.StartsWith("Right")).ToList();
            Assert.Equal(left.Count, right.Count);
            Assert.Equal((count - 1) / 2, left.Count);

            foreach (var tile in left.Concat(right))
                Assert.True(masterArea > (long)tile.Width * tile.Height,
                    $"Master must out-size tile {tile.Label} at {count}/{w}x{h}");

            // Left column sits on the left edge, right column on the right edge, both full height.
            Assert.All(left, t => Assert.Equal(0, t.X));
            Assert.All(right, t => Assert.Equal(w - right[0].Width, t.X));
            Assert.Equal(h, left.Sum(t => t.Height));
            Assert.Equal(h, right.Sum(t => t.Height));

            // Master sits centred between the two columns, full height, on the monitor.
            Assert.Equal(0, master.Y);
            Assert.Equal(h, master.Height);
            Assert.InRange(master.X, 0, w - master.Width);
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    public void TwinStack_TilesMatchMasterAspect_NoPreviewBars(int count)
    {
        var profile = Regenerate(count, 2560, 1440);
        var master = profile.Slots.Single(s => s.SlotNumber == count);
        var masterAspect = (double)master.Width / master.Height;

        foreach (var tile in profile.Slots.Where(s => s.SlotNumber != count))
        {
            var tileAspect = (double)tile.Width / tile.Height;
            Assert.True(Math.Abs(tileAspect - masterAspect) < 0.05,
                $"Tile {tile.Label} aspect {tileAspect:F3} should match master aspect {masterAspect:F3}");
        }
    }

    [Fact]
    public void TwinStack_OddCountRequest_SnapsToNearestEvenAltCount()
    {
        // 6 isn't in TwinStackCounts (5,7,9); NearestCount should snap to 5 or 7.
        var profile = Regenerate(6, 2560, 1440);
        Assert.Contains(profile.TemplateCount, new[] { 5, 7 });
    }

    [Fact]
    public void BuiltIns_IncludeTwinStack()
    {
        var profiles = PresetFactory.CreateBuiltInProfiles();

        var twinStack = profiles.Single(p => p.Name == "Twin Stack");
        Assert.True(twinStack.IsFamilyTemplate);
        Assert.Equal("Twin Stack", twinStack.Category);
        Assert.Equal(5, twinStack.Slots.Count);
    }
}
