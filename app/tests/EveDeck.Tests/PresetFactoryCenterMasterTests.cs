using Xunit;
using EveDeck.Services;
using EveDeck.Models;
using System.Collections.ObjectModel;

namespace EveDeck.Tests;

public class PresetFactoryCenterMasterTests
{
    [Fact]
    public void CenterMaster_5At2560x1440_MasterShrunkTo60Percent()
    {
        // 2026-07-08: tiles are back to the original full 2x2 grid (legacy behavior); only the master's
        // size changed, from 1.5 cells (75%, matching the pre-existing legacy 5-Char master exactly) down
        // to 1.2 cells (60%) so it covers less of each corner tile while the grid itself is untouched.
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Center Master",
            TemplateWidth = 2560,
            TemplateHeight = 1440,
            TemplateCount = 5
        };

        PresetFactory.RegenerateFamilySlots(profile);

        Assert.Equal(5, profile.Slots.Count);

        var slots = profile.Slots.OrderBy(s => s.SlotNumber).ToList();

        Assert.Equal(1, slots[0].SlotNumber);
        Assert.Equal("Top Left", slots[0].Label);
        Assert.Equal((0, 0, 1280, 720), (slots[0].X, slots[0].Y, slots[0].Width, slots[0].Height));

        Assert.Equal(2, slots[1].SlotNumber);
        Assert.Equal("Top Right", slots[1].Label);
        Assert.Equal((1280, 0, 1280, 720), (slots[1].X, slots[1].Y, slots[1].Width, slots[1].Height));

        Assert.Equal(3, slots[2].SlotNumber);
        Assert.Equal("Bottom Left", slots[2].Label);
        Assert.Equal((0, 720, 1280, 720), (slots[2].X, slots[2].Y, slots[2].Width, slots[2].Height));

        Assert.Equal(4, slots[3].SlotNumber);
        Assert.Equal("Bottom Right", slots[3].Label);
        Assert.Equal((1280, 720, 1280, 720), (slots[3].X, slots[3].Y, slots[3].Width, slots[3].Height));

        Assert.Equal(5, slots[4].SlotNumber);
        Assert.Equal("Master", slots[4].Label);
        Assert.Equal((512, 288, 1536, 864), (slots[4].X, slots[4].Y, slots[4].Width, slots[4].Height));
    }

    [Theory]
    [InlineData(4, 1920, 1080)]
    [InlineData(5, 1920, 1080)]
    [InlineData(6, 1920, 1080)]
    [InlineData(7, 1920, 1080)]
    [InlineData(8, 1920, 1080)]
    [InlineData(9, 1920, 1080)]
    [InlineData(10, 1920, 1080)]
    [InlineData(12, 1920, 1080)]
    [InlineData(15, 1920, 1080)]
    [InlineData(4, 2560, 1440)]
    [InlineData(5, 2560, 1440)]
    [InlineData(6, 2560, 1440)]
    [InlineData(7, 2560, 1440)]
    [InlineData(8, 2560, 1440)]
    [InlineData(9, 2560, 1440)]
    [InlineData(10, 2560, 1440)]
    [InlineData(12, 2560, 1440)]
    [InlineData(15, 2560, 1440)]
    [InlineData(4, 3200, 1800)]
    [InlineData(5, 3200, 1800)]
    [InlineData(6, 3200, 1800)]
    [InlineData(7, 3200, 1800)]
    [InlineData(8, 3200, 1800)]
    [InlineData(9, 3200, 1800)]
    [InlineData(10, 3200, 1800)]
    [InlineData(12, 3200, 1800)]
    [InlineData(15, 3200, 1800)]
    [InlineData(4, 3840, 2160)]
    [InlineData(5, 3840, 2160)]
    [InlineData(6, 3840, 2160)]
    [InlineData(7, 3840, 2160)]
    [InlineData(8, 3840, 2160)]
    [InlineData(9, 3840, 2160)]
    [InlineData(10, 3840, 2160)]
    [InlineData(12, 3840, 2160)]
    [InlineData(15, 3840, 2160)]
    public void CenterMaster_MasterIsStrictlyLargestSlot(int count, int width, int height)
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Center Master",
            TemplateWidth = width,
            TemplateHeight = height,
            TemplateCount = count
        };

        PresetFactory.RegenerateFamilySlots(profile);

        var masterSlot = profile.Slots.Single(s => s.SlotNumber == count);
        long masterArea = (long)masterSlot.Width * masterSlot.Height;

        foreach (var slot in profile.Slots.Where(s => s.SlotNumber != count))
        {
            long slotArea = (long)slot.Width * slot.Height;
            Assert.True(masterArea > slotArea,
                $"Master area {masterArea} should be strictly larger than slot {slot.SlotNumber} area {slotArea}");
        }
    }

    [Fact]
    public void CenterMaster_PerimeterFullyCovered()
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Center Master",
            TemplateWidth = 2560,
            TemplateHeight = 1440,
            TemplateCount = 9
        };

        PresetFactory.RegenerateFamilySlots(profile);

        var nonMasterSlots = profile.Slots.Where(s => s.SlotNumber != 9).ToList();

        var topSlots = nonMasterSlots.Where(s => s.Y == 0).OrderBy(s => s.X).ToList();
        var bottomSlots = nonMasterSlots.Where(s => s.Y + s.Height == profile.TemplateHeight).OrderBy(s => s.X).ToList();

        Assert.NotEmpty(topSlots);
        Assert.NotEmpty(bottomSlots);

        int topWidth = topSlots.Sum(s => s.Width);
        int bottomWidth = bottomSlots.Sum(s => s.Width);

        Assert.Equal(profile.TemplateWidth, topWidth);
        Assert.Equal(profile.TemplateWidth, bottomWidth);
    }

    [Theory]
    [InlineData(4, 1920, 1080)]
    [InlineData(5, 1920, 1080)]
    [InlineData(6, 1920, 1080)]
    [InlineData(7, 1920, 1080)]
    [InlineData(8, 1920, 1080)]
    [InlineData(9, 1920, 1080)]
    [InlineData(10, 1920, 1080)]
    [InlineData(12, 1920, 1080)]
    [InlineData(15, 1920, 1080)]
    [InlineData(4, 2560, 1440)]
    [InlineData(5, 2560, 1440)]
    [InlineData(6, 2560, 1440)]
    [InlineData(7, 2560, 1440)]
    [InlineData(8, 2560, 1440)]
    [InlineData(9, 2560, 1440)]
    [InlineData(10, 2560, 1440)]
    [InlineData(12, 2560, 1440)]
    [InlineData(15, 2560, 1440)]
    [InlineData(4, 3200, 1800)]
    [InlineData(5, 3200, 1800)]
    [InlineData(6, 3200, 1800)]
    [InlineData(7, 3200, 1800)]
    [InlineData(8, 3200, 1800)]
    [InlineData(9, 3200, 1800)]
    [InlineData(10, 3200, 1800)]
    [InlineData(12, 3200, 1800)]
    [InlineData(15, 3200, 1800)]
    [InlineData(4, 3840, 2160)]
    [InlineData(5, 3840, 2160)]
    [InlineData(6, 3840, 2160)]
    [InlineData(7, 3840, 2160)]
    [InlineData(8, 3840, 2160)]
    [InlineData(9, 3840, 2160)]
    [InlineData(10, 3840, 2160)]
    [InlineData(12, 3840, 2160)]
    [InlineData(15, 3840, 2160)]
    public void CenterMaster_SlotCountAndNumbering(int count, int width, int height)
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Center Master",
            TemplateWidth = width,
            TemplateHeight = height,
            TemplateCount = count
        };

        PresetFactory.RegenerateFamilySlots(profile);

        Assert.Equal(count, profile.Slots.Count);

        var slotNumbers = profile.Slots.Select(s => s.SlotNumber).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range(1, count).ToList(), slotNumbers);
    }

    [Fact]
    public void CenterMaster_9_ProducesFull3x3Ring()
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Center Master",
            TemplateWidth = 2560,
            TemplateHeight = 1440,
            TemplateCount = 9
        };

        PresetFactory.RegenerateFamilySlots(profile);

        Assert.Equal(9, profile.Slots.Count);

        var masterSlot = profile.Slots.Single(s => s.SlotNumber == 9);
        Assert.Equal("Master", masterSlot.Label);

        int masterCenterX = masterSlot.X + masterSlot.Width / 2;
        int masterCenterY = masterSlot.Y + masterSlot.Height / 2;

        int profileCenterX = profile.TemplateWidth / 2;
        int profileCenterY = profile.TemplateHeight / 2;

        Assert.True(Math.Abs(masterCenterX - profileCenterX) <= 1,
            $"Master center X {masterCenterX} should be near profile center {profileCenterX}");
        Assert.True(Math.Abs(masterCenterY - profileCenterY) <= 1,
            $"Master center Y {masterCenterY} should be near profile center {profileCenterY}");
    }

    [Fact]
    public void EvenSplit_Basics()
    {
        var result = PresetFactory.EvenSplit(10, 3);
        Assert.Equal(3, result.Count);
        Assert.Equal((0, 3), result[0]);
        Assert.Equal((3, 3), result[1]);
        Assert.Equal((6, 4), result[2]);

        var result2560_2 = PresetFactory.EvenSplit(2560, 2);
        Assert.Equal(2, result2560_2.Count);
        Assert.Equal((0, 1280), result2560_2[0]);
        Assert.Equal((1280, 1280), result2560_2[1]);

        var resultEmpty = PresetFactory.EvenSplit(100, 0);
        Assert.Empty(resultEmpty);
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(100, 2)]
    [InlineData(2560, 3)]
    [InlineData(1440, 4)]
    [InlineData(3200, 5)]
    [InlineData(1920, 7)]
    public void EvenSplit_ContiguousAndSumsToTotal(int total, int count)
    {
        var result = PresetFactory.EvenSplit(total, count);

        Assert.Equal(count, result.Count);

        int summedSize = 0;
        for (int i = 0; i < result.Count; i++)
        {
            var (offset, size) = result[i];
            Assert.Equal(summedSize, offset);
            summedSize += size;
        }

        Assert.Equal(total, summedSize);
    }
}
