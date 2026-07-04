using Xunit;
using EveDeck.Models;
using System.Collections.ObjectModel;

namespace EveDeck.Tests;

public class LayoutProfileCloneTests
{
    [Fact]
    public void Clone_DeepCopiesSlots()
    {
        var original = new LayoutProfile
        {
            Name = "Test Profile",
            Slots = new ObservableCollection<LayoutSlot>
            {
                new LayoutSlot
                {
                    SlotNumber = 1,
                    Label = "Slot 1",
                    X = 0,
                    Y = 0,
                    Width = 100,
                    Height = 100,
                    Borderless = true,
                    MonitorId = "MON1",
                    HomeSeat = 1
                },
                new LayoutSlot
                {
                    SlotNumber = 2,
                    Label = "Slot 2",
                    X = 100,
                    Y = 0,
                    Width = 100,
                    Height = 100,
                    Borderless = false,
                    MonitorId = "MON2",
                    HomeSeat = null
                }
            }
        };

        var clone = original.Clone();

        Assert.Equal(2, clone.Slots.Count);

        for (int i = 0; i < original.Slots.Count; i++)
        {
            var origSlot = original.Slots[i];
            var cloneSlot = clone.Slots[i];

            Assert.Equal(origSlot.SlotNumber, cloneSlot.SlotNumber);
            Assert.Equal(origSlot.Label, cloneSlot.Label);
            Assert.Equal(origSlot.X, cloneSlot.X);
            Assert.Equal(origSlot.Y, cloneSlot.Y);
            Assert.Equal(origSlot.Width, cloneSlot.Width);
            Assert.Equal(origSlot.Height, cloneSlot.Height);
            Assert.Equal(origSlot.Borderless, cloneSlot.Borderless);
            Assert.Equal(origSlot.MonitorId, cloneSlot.MonitorId);
            Assert.Equal(origSlot.HomeSeat, cloneSlot.HomeSeat);
        }

        clone.Slots[0].X = 999;
        Assert.Equal(0, original.Slots[0].X);
    }

    [Fact]
    public void Clone_ResetsIdentityFlags()
    {
        var original = new LayoutProfile
        {
            Name = "Test Profile",
            IsBuiltIn = true,
            Category = "Grid",
            IsFamilyTemplate = true
        };

        var originalId = original.Id;

        var clone = original.Clone();

        Assert.False(clone.IsBuiltIn);
        Assert.Equal("Custom", clone.Category);
        Assert.False(clone.IsFamilyTemplate);
        Assert.NotEqual(originalId, clone.Id);
        Assert.Equal("Test Profile Copy", clone.Name);
    }

    [Fact]
    public void Clone_CopiesPlacementMetadata()
    {
        var original = new LayoutProfile
        {
            Name = "Test",
            MasterSeat = 5,
            AvoidTaskbar = true,
            CaptureMonitorX = 100,
            CaptureMonitorY = 50,
            CaptureMonitorWidth = 2560,
            CaptureMonitorHeight = 1440
        };

        var clone = original.Clone();

        Assert.Equal(5, clone.MasterSeat);
        Assert.True(clone.AvoidTaskbar);
        Assert.Equal(100, clone.CaptureMonitorX);
        Assert.Equal(50, clone.CaptureMonitorY);
        Assert.Equal(2560, clone.CaptureMonitorWidth);
        Assert.Equal(1440, clone.CaptureMonitorHeight);
    }

    [Fact]
    public void Clone_DeepCopiesSwapGroups()
    {
        var original = new LayoutProfile
        {
            Name = "Test",
            SwapGroups = new ObservableCollection<SwapGroup>
            {
                new SwapGroup { Name = "Group 1", SlotNumbers = new List<int> { 1, 2, 3 } }
            }
        };

        var clone = original.Clone();

        Assert.Single(clone.SwapGroups);
        Assert.Equal("Group 1", clone.SwapGroups[0].Name);
        Assert.Equal(new[] { 1, 2, 3 }, clone.SwapGroups[0].SlotNumbers);

        clone.SwapGroups[0].SlotNumbers.Add(4);
        Assert.Equal(3, original.SwapGroups[0].SlotNumbers.Count);
        Assert.Equal(4, clone.SwapGroups[0].SlotNumbers.Count);
    }

    [Fact]
    public void Clone_DoesNotCopyMasterResolution_CurrentBehavior()
    {
        var original = new LayoutProfile
        {
            Name = "Test",
            MasterResolutionWidth = 1920,
            MasterResolutionHeight = 1080
        };

        var clone = original.Clone();

        // This documents the current behavior: Master resolution is not copied.
        // This may be a bug, but we document it as-is.
        Assert.Equal(0, clone.MasterResolutionWidth);
        Assert.Equal(0, clone.MasterResolutionHeight);
    }

    [Fact]
    public void SupportsCornerGrid_FalseForStackedTrueForGrid()
    {
        var stacked = new LayoutProfile
        {
            Name = "Stacked",
            Slots = new ObservableCollection<LayoutSlot>
            {
                new LayoutSlot { X = 0, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { X = 0, Y = 0, Width = 100, Height = 100 }
            }
        };

        Assert.False(stacked.SupportsCornerGrid);

        var grid = new LayoutProfile
        {
            Name = "Grid",
            Slots = new ObservableCollection<LayoutSlot>
            {
                new LayoutSlot { X = 0, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { X = 100, Y = 0, Width = 100, Height = 100 }
            }
        };

        Assert.True(grid.SupportsCornerGrid);

        var gridMulti = new LayoutProfile
        {
            Name = "Grid Multi",
            Slots = new ObservableCollection<LayoutSlot>
            {
                new LayoutSlot { X = 0, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { X = 100, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { X = 0, Y = 100, Width = 100, Height = 100 },
                new LayoutSlot { X = 100, Y = 100, Width = 100, Height = 100 }
            }
        };

        Assert.True(gridMulti.SupportsCornerGrid);
    }
}
