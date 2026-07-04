using Xunit;
using EveDeck.Services;
using EveDeck.Models;
using System.Collections.ObjectModel;

namespace EveDeck.Tests;

public class PresetFactoryGridTests
{
    [Fact]
    public void Grid_4At2560x1440_ProducesExact2x2()
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Grid",
            TemplateWidth = 2560,
            TemplateHeight = 1440,
            TemplateCount = 4
        };

        PresetFactory.RegenerateFamilySlots(profile);

        Assert.Equal(4, profile.Slots.Count);

        var rects = profile.Slots.Select(s => (s.SlotNumber, s.X, s.Y, s.Width, s.Height, s.Borderless)).ToList();

        Assert.Collection(rects,
            s => Assert.Equal((1, 0, 0, 1280, 720, true), s),
            s => Assert.Equal((2, 1280, 0, 1280, 720, true), s),
            s => Assert.Equal((3, 0, 720, 1280, 720, true), s),
            s => Assert.Equal((4, 1280, 720, 1280, 720, true), s)
        );

        int totalArea = profile.Slots.Sum(s => s.Width * s.Height);
        Assert.Equal(2560 * 1440, totalArea);

        for (int i = 0; i < profile.Slots.Count - 1; i++)
        {
            for (int j = i + 1; j < profile.Slots.Count; j++)
            {
                Assert.False(RectsOverlap(profile.Slots[i], profile.Slots[j]));
            }
        }
    }

    [Theory]
    [InlineData(1920, 1080, 2)]
    [InlineData(1920, 1080, 3)]
    [InlineData(1920, 1080, 4)]
    [InlineData(1920, 1080, 5)]
    [InlineData(1920, 1080, 6)]
    [InlineData(1920, 1080, 7)]
    [InlineData(1920, 1080, 8)]
    [InlineData(1920, 1080, 9)]
    [InlineData(1920, 1080, 10)]
    [InlineData(1920, 1080, 12)]
    [InlineData(1920, 1080, 15)]
    [InlineData(2560, 1440, 2)]
    [InlineData(2560, 1440, 3)]
    [InlineData(2560, 1440, 4)]
    [InlineData(2560, 1440, 5)]
    [InlineData(2560, 1440, 6)]
    [InlineData(2560, 1440, 7)]
    [InlineData(2560, 1440, 8)]
    [InlineData(2560, 1440, 9)]
    [InlineData(2560, 1440, 10)]
    [InlineData(2560, 1440, 12)]
    [InlineData(2560, 1440, 15)]
    [InlineData(3200, 1800, 2)]
    [InlineData(3200, 1800, 3)]
    [InlineData(3200, 1800, 4)]
    [InlineData(3200, 1800, 5)]
    [InlineData(3200, 1800, 6)]
    [InlineData(3200, 1800, 7)]
    [InlineData(3200, 1800, 8)]
    [InlineData(3200, 1800, 9)]
    [InlineData(3200, 1800, 10)]
    [InlineData(3200, 1800, 12)]
    [InlineData(3200, 1800, 15)]
    [InlineData(3840, 2160, 2)]
    [InlineData(3840, 2160, 3)]
    [InlineData(3840, 2160, 4)]
    [InlineData(3840, 2160, 5)]
    [InlineData(3840, 2160, 6)]
    [InlineData(3840, 2160, 7)]
    [InlineData(3840, 2160, 8)]
    [InlineData(3840, 2160, 9)]
    [InlineData(3840, 2160, 10)]
    [InlineData(3840, 2160, 12)]
    [InlineData(3840, 2160, 15)]
    public void Grid_AllCombos_TileScreenExactly(int width, int height, int count)
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Grid",
            TemplateWidth = width,
            TemplateHeight = height,
            TemplateCount = count
        };

        PresetFactory.RegenerateFamilySlots(profile);

        Assert.Equal(count, profile.Slots.Count);

        var slotNumbers = profile.Slots.Select(s => s.SlotNumber).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range(1, count).ToList(), slotNumbers);

        foreach (var slot in profile.Slots)
        {
            Assert.True(slot.X >= 0 && slot.X < width);
            Assert.True(slot.Y >= 0 && slot.Y < height);
            Assert.True(slot.X + slot.Width <= width);
            Assert.True(slot.Y + slot.Height <= height);
        }

        for (int i = 0; i < profile.Slots.Count - 1; i++)
        {
            for (int j = i + 1; j < profile.Slots.Count; j++)
            {
                Assert.False(RectsOverlap(profile.Slots[i], profile.Slots[j]));
            }
        }

        int totalArea = profile.Slots.Sum(s => s.Width * s.Height);
        Assert.Equal(width * height, totalArea);
    }

    [Fact]
    public void Grid_PartialLastRow_SpansFullWidth()
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Grid",
            TemplateWidth = 2560,
            TemplateHeight = 1440,
            TemplateCount = 5
        };

        PresetFactory.RegenerateFamilySlots(profile);

        Assert.Equal(5, profile.Slots.Count);

        var topRowSlots = profile.Slots.Where(s => s.Y == 0).OrderBy(s => s.X).ToList();
        Assert.Equal(3, topRowSlots.Count);

        var bottomRowSlots = profile.Slots.Where(s => s.Y > 0).OrderBy(s => s.X).ToList();
        Assert.Equal(2, bottomRowSlots.Count);

        int bottomRowWidth = bottomRowSlots.Sum(s => s.Width);
        Assert.Equal(2560, bottomRowWidth);
    }

    [Fact]
    public void RegenerateFamilySlots_ClampsUnknownResolutionAndCount()
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Grid",
            TemplateWidth = 1000,
            TemplateHeight = 500,
            TemplateCount = 11
        };

        PresetFactory.RegenerateFamilySlots(profile);

        Assert.Equal(1920, profile.TemplateWidth);
        Assert.Equal(1080, profile.TemplateHeight);

        Assert.Equal(12, profile.TemplateCount);
    }

    [Fact]
    public void RegenerateFamilySlots_NonFamilyTemplate_IsNoOp()
    {
        var profile = new LayoutProfile
        {
            IsFamilyTemplate = false,
            Category = "Custom",
            Slots = new ObservableCollection<LayoutSlot>
            {
                new LayoutSlot { SlotNumber = 1, X = 0, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { SlotNumber = 2, X = 100, Y = 0, Width = 100, Height = 100 }
            }
        };

        var originalCount = profile.Slots.Count;
        var originalSlot1 = profile.Slots[0];

        PresetFactory.RegenerateFamilySlots(profile);

        Assert.Equal(originalCount, profile.Slots.Count);
        Assert.Same(originalSlot1, profile.Slots[0]);
        Assert.Equal(0, profile.Slots[0].X);
    }

    private static bool RectsOverlap(LayoutSlot a, LayoutSlot b)
    {
        return !(a.X + a.Width <= b.X || b.X + b.Width <= a.X ||
                 a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y);
    }
}
