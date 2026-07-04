using Xunit;
using EveWindowCommander.Services;
using EveWindowCommander.Models;

namespace EveWindowCommander.Tests;

public class PresetFactoryWhammySideStackTests
{
    private static LayoutProfile Regenerate(string category, int count, int width, int height, string side = "")
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = category,
            TemplateWidth = width,
            TemplateHeight = height,
            TemplateCount = count,
            TemplateSide = side
        };
        PresetFactory.RegenerateFamilySlots(profile);
        return profile;
    }

    // ── Whammy Board ─────────────────────────────────────────────────────────────

    [Fact]
    public void Whammy_5At2560x1440_FullWidthRowsAndFillingMaster()
    {
        var profile = Regenerate("Whammy Board", 5, 2560, 1440);

        Assert.Equal(5, profile.Slots.Count);
        var slots = profile.Slots.OrderBy(s => s.SlotNumber).ToList();

        // Row height clamps to 1/4 of the monitor (360) for small rows.
        Assert.Equal("Top Left", slots[0].Label);
        Assert.Equal((0, 0, 1280, 360), (slots[0].X, slots[0].Y, slots[0].Width, slots[0].Height));

        Assert.Equal("Top Right", slots[1].Label);
        Assert.Equal((1280, 0, 1280, 360), (slots[1].X, slots[1].Y, slots[1].Width, slots[1].Height));

        Assert.Equal("Bottom Left", slots[2].Label);
        Assert.Equal((0, 1080, 1280, 360), (slots[2].X, slots[2].Y, slots[2].Width, slots[2].Height));

        Assert.Equal("Bottom Right", slots[3].Label);
        Assert.Equal((1280, 1080, 1280, 360), (slots[3].X, slots[3].Y, slots[3].Width, slots[3].Height));

        // Master fills the whole band between the rows, edge to edge (Press Your Luck style).
        Assert.Equal("Master", slots[4].Label);
        Assert.Equal((0, 360, 2560, 720), (slots[4].X, slots[4].Y, slots[4].Width, slots[4].Height));
    }

    [Fact]
    public void Whammy_4_OddAltGoesTop_BottomTileSpansFullWidth()
    {
        var profile = Regenerate("Whammy Board", 4, 2560, 1440);

        var slots = profile.Slots.OrderBy(s => s.SlotNumber).ToList();
        Assert.Equal(4, slots.Count);

        // Two top tiles spanning the full width, one full-width bottom tile.
        Assert.Equal(0, slots[0].Y);
        Assert.Equal(0, slots[1].Y);
        Assert.Equal("Bottom", slots[2].Label);
        Assert.Equal(1080, slots[2].Y);
        Assert.Equal(0, slots[2].X);
        Assert.Equal(2560, slots[2].Width);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(15)]
    public void Whammy_AllCounts_MasterIsStrictlyLargestAndCentred(int count)
    {
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440), (3200, 1800), (3840, 2160) })
        {
            var profile = Regenerate("Whammy Board", count, w, h);

            Assert.Equal(count, profile.Slots.Count);
            Assert.Equal(Enumerable.Range(1, count), profile.Slots.Select(s => s.SlotNumber).OrderBy(n => n));

            var master = profile.Slots.Single(s => s.SlotNumber == count);
            Assert.Equal("Master", master.Label);
            long masterArea = (long)master.Width * master.Height;
            foreach (var slot in profile.Slots.Where(s => s.SlotNumber != count))
                Assert.True(masterArea > (long)slot.Width * slot.Height,
                    $"Master must out-size slot {slot.SlotNumber} at {count}/{w}x{h}");

            Assert.True(Math.Abs(master.X + master.Width / 2 - w / 2) <= 1);
            Assert.True(Math.Abs(master.Y + master.Height / 2 - h / 2) <= 1);

            // Alt tiles touch only the top and bottom edges and stay on the monitor.
            foreach (var slot in profile.Slots.Where(s => s.SlotNumber != count))
            {
                Assert.True(slot.Y == 0 || slot.Y + slot.Height == h,
                    $"Tile {slot.SlotNumber} must sit on the top or bottom edge");
                Assert.InRange(slot.X, 0, w - slot.Width);
            }
        }
    }

    [Fact]
    public void Whammy_15_FullRowsSpanEntireWidth()
    {
        var profile = Regenerate("Whammy Board", 15, 2560, 1440);

        var top = profile.Slots.Where(s => s.SlotNumber != 15 && s.Y == 0).ToList();
        var bottom = profile.Slots.Where(s => s.SlotNumber != 15 && s.Y != 0).ToList();

        Assert.Equal(7, top.Count);
        Assert.Equal(7, bottom.Count);
        Assert.Equal(2560, top.Sum(s => s.Width));
        Assert.Equal(2560, bottom.Sum(s => s.Width));
    }

    // ── Side Stack ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    public void SideStack_5At2560x1440_StackOnChosenEdgeMasterOpposite(string side)
    {
        var profile = Regenerate("Side Stack", 5, 2560, 1440, side);

        Assert.Equal(5, profile.Slots.Count);
        var tiles = profile.Slots.Where(s => s.SlotNumber != 5).OrderBy(s => s.SlotNumber).ToList();
        var master = profile.Slots.Single(s => s.SlotNumber == 5);

        // 4 tiles => tileW = sw/5 = 512, 512x360 tiles filling the full edge - the exact aspect
        // ratio of the 2048x1440 master, so previews have no black bars.
        var expectedTileX = side == "Left" ? 0 : 2560 - 512;
        foreach (var tile in tiles)
        {
            Assert.Equal(expectedTileX, tile.X);
            Assert.Equal(512, tile.Width);
            Assert.Equal(360, tile.Height);
        }
        Assert.Equal(1440, tiles.Sum(t => t.Height));
        Assert.Equal(new[] { $"{side} 1", $"{side} 2", $"{side} 3", $"{side} 4" }, tiles.Select(t => t.Label));

        // Master fills all remaining space: full height, edge to edge against the tile column.
        Assert.Equal("Master", master.Label);
        Assert.Equal(2048, master.Width);
        Assert.Equal(1440, master.Height);
        Assert.Equal(side == "Left" ? 512 : 0, master.X);
        Assert.Equal(0, master.Y);
    }

    [Fact]
    public void SideStack_3_TwoTilesFillTheFullEdge()
    {
        var profile = Regenerate("Side Stack", 3, 2560, 1440, "Left");

        var tiles = profile.Slots.Where(s => s.SlotNumber != 3).OrderBy(s => s.Y).ToList();
        Assert.Equal(2, tiles.Count);

        // tileW = sw/3: 853x720 tiles spanning the whole edge, aspect-matched to the 1707x1440 master.
        Assert.Equal(853, tiles[0].Width);
        Assert.Equal(720, tiles[0].Height);
        Assert.Equal(0, tiles[0].Y);
        Assert.Equal(720, tiles[1].Y);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void SideStack_TilesMatchMasterAspect_NoPreviewBars(int count)
    {
        var profile = Regenerate("Side Stack", count, 2560, 1440, "Left");
        var master = profile.Slots.Single(s => s.SlotNumber == count);
        var masterAspect = (double)master.Width / master.Height;

        foreach (var tile in profile.Slots.Where(s => s.SlotNumber != count))
        {
            var tileAspect = (double)tile.Width / tile.Height;
            // 0.05 tolerance: the last tile absorbs the EvenSplit rounding remainder (a few px).
            Assert.True(Math.Abs(tileAspect - masterAspect) < 0.05,
                $"Tile {tile.SlotNumber} aspect {tileAspect:F3} should match master aspect {masterAspect:F3}");
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void SideStack_AllCounts_MasterIsStrictlyLargestAndOnMonitor(int count)
    {
        foreach (var side in new[] { "Left", "Right" })
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440), (3200, 1800), (3840, 2160) })
        {
            var profile = Regenerate("Side Stack", count, w, h, side);

            Assert.Equal(count, profile.Slots.Count);
            Assert.Equal(Enumerable.Range(1, count), profile.Slots.Select(s => s.SlotNumber).OrderBy(n => n));

            var master = profile.Slots.Single(s => s.SlotNumber == count);
            long masterArea = (long)master.Width * master.Height;
            foreach (var slot in profile.Slots)
            {
                if (slot.SlotNumber != count)
                    Assert.True(masterArea > (long)slot.Width * slot.Height,
                        $"Master must out-size slot {slot.SlotNumber} at {count}/{w}x{h}/{side}");
                Assert.InRange(slot.X, 0, w - slot.Width);
                Assert.InRange(slot.Y, 0, h - slot.Height);
            }
        }
    }

    [Fact]
    public void SideStack_EmptySide_DefaultsToLeft()
    {
        var profile = Regenerate("Side Stack", 5, 2560, 1440, "");
        Assert.Equal("Left", profile.TemplateSide);
        Assert.Equal(0, profile.Slots.OrderBy(s => s.SlotNumber).First().X);
    }

    // ── Built-ins ────────────────────────────────────────────────────────────────

    [Fact]
    public void BuiltIns_IncludeBothNewFamilies()
    {
        var profiles = PresetFactory.CreateBuiltInProfiles();

        var whammy = profiles.Single(p => p.Name == "Whammy Board");
        Assert.True(whammy.IsFamilyTemplate);
        Assert.Equal("Whammy Board", whammy.Category);
        Assert.Equal(5, whammy.Slots.Count);

        var sideStack = profiles.Single(p => p.Name == "Side Stack");
        Assert.True(sideStack.IsFamilyTemplate);
        Assert.Equal("Side Stack", sideStack.Category);
        Assert.Equal("Left", sideStack.TemplateSide);
        Assert.Equal(5, sideStack.Slots.Count);
    }
}
