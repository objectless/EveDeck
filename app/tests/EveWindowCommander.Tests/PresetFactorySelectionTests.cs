using Xunit;
using EveWindowCommander.Services;
using EveWindowCommander.Models;
using System.Collections.ObjectModel;

namespace EveWindowCommander.Tests;

public class PresetFactorySelectionTests
{
    [Fact]
    public void ResolveFamilySelection_ZeroOrOneClients_ReturnsNull()
    {
        var result0 = PresetFactory.ResolveFamilySelection(0, 2560, 1440);
        Assert.Null(result0);

        var result1 = PresetFactory.ResolveFamilySelection(1, 2560, 1440);
        Assert.Null(result1);
    }

    [Fact]
    public void ResolveFamilySelection_4To15_PicksCenterMaster()
    {
        var result4 = PresetFactory.ResolveFamilySelection(4, 2560, 1440);
        Assert.NotNull(result4);
        Assert.Equal("Center Master", result4.Value.Category);

        var result5 = PresetFactory.ResolveFamilySelection(5, 2560, 1440);
        Assert.NotNull(result5);
        Assert.Equal("Center Master", result5.Value.Category);
        Assert.Equal(2560, result5.Value.Width);
        Assert.Equal(1440, result5.Value.Height);
        Assert.Equal(5, result5.Value.Count);

        var result15 = PresetFactory.ResolveFamilySelection(15, 2560, 1440);
        Assert.NotNull(result15);
        Assert.Equal("Center Master", result15.Value.Category);
    }

    [Fact]
    public void ResolveFamilySelection_2And3AndOver15_PicksGrid()
    {
        var result2 = PresetFactory.ResolveFamilySelection(2, 2560, 1440);
        Assert.NotNull(result2);
        Assert.Equal("Grid", result2.Value.Category);
        Assert.Equal(2, result2.Value.Count);

        var result3 = PresetFactory.ResolveFamilySelection(3, 2560, 1440);
        Assert.NotNull(result3);
        Assert.Equal("Grid", result3.Value.Category);
        Assert.Equal(3, result3.Value.Count);

        var result20 = PresetFactory.ResolveFamilySelection(20, 2560, 1440);
        Assert.NotNull(result20);
        Assert.Equal("Grid", result20.Value.Category);
        Assert.Equal(15, result20.Value.Count);
    }

    [Fact]
    public void ResolveFamilySelection_SnapsToNearestResolutionByWidth()
    {
        var result = PresetFactory.ResolveFamilySelection(5, 2000, 1600);
        Assert.NotNull(result);
        Assert.Equal(1920, result.Value.Width);
        Assert.Equal(1080, result.Value.Height);

        var result2 = PresetFactory.ResolveFamilySelection(5, 2700, 1500);
        Assert.NotNull(result2);
        Assert.Equal(2560, result2.Value.Width);
        Assert.Equal(1440, result2.Value.Height);
    }

    [Fact]
    public void EnsureBuiltInProfiles_RemovesDeprecatedAndPreservesFamilyChoices()
    {
        var profiles = new List<LayoutProfile>
        {
            new LayoutProfile
            {
                Name = "2x2 1920x1080 - four 960x540",
                IsBuiltIn = true
            },
            new LayoutProfile
            {
                Name = "Grid",
                IsBuiltIn = true,
                Category = "Grid",
                IsFamilyTemplate = true,
                TemplateWidth = 3840,
                TemplateHeight = 2160,
                TemplateCount = 6
            }
        };

        PresetFactory.EnsureBuiltInProfiles(profiles);

        var deprecated = profiles.FirstOrDefault(p => p.Name == "2x2 1920x1080 - four 960x540");
        Assert.Null(deprecated);

        var gridProfile = profiles.FirstOrDefault(p => p.Name == "Grid" && p.IsFamilyTemplate);
        Assert.NotNull(gridProfile);
        Assert.Equal(3840, gridProfile.TemplateWidth);
        Assert.Equal(2160, gridProfile.TemplateHeight);
        Assert.Equal(6, gridProfile.TemplateCount);

        Assert.Equal(6, gridProfile.Slots.Count);

        var expectedSlots = new LayoutProfile
        {
            IsFamilyTemplate = true,
            Category = "Grid",
            TemplateWidth = 3840,
            TemplateHeight = 2160,
            TemplateCount = 6
        };
        PresetFactory.RegenerateFamilySlots(expectedSlots);

        Assert.Equal(expectedSlots.Slots.Count, gridProfile.Slots.Count);
        for (int i = 0; i < expectedSlots.Slots.Count; i++)
        {
            Assert.Equal(expectedSlots.Slots[i].SlotNumber, gridProfile.Slots[i].SlotNumber);
            Assert.Equal(expectedSlots.Slots[i].X, gridProfile.Slots[i].X);
            Assert.Equal(expectedSlots.Slots[i].Y, gridProfile.Slots[i].Y);
            Assert.Equal(expectedSlots.Slots[i].Width, gridProfile.Slots[i].Width);
            Assert.Equal(expectedSlots.Slots[i].Height, gridProfile.Slots[i].Height);
        }
    }
}
