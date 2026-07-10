using Xunit;
using EveDeck.Models;
using EveDeck.ViewModels;
using System.Collections.ObjectModel;
using System.Reflection;

namespace EveDeck.Tests;

public class MultiMonitorProfileTests
{
    private static bool IsMultiMonitorProfile(LayoutProfile profile)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "IsMultiMonitorProfile",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)method!.Invoke(null, new object[] { profile })!;
    }

    [Fact]
    public void IsMultiMonitorProfile_MultipleDistinctMonitorIds_ReturnsTrue()
    {
        var profile = new LayoutProfile
        {
            IsBuiltIn = false,
            Slots = new ObservableCollection<LayoutSlot>
            {
                new LayoutSlot { SlotNumber = 1, MonitorId = "MON1", X = 0, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { SlotNumber = 2, MonitorId = "MON1", X = 100, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { SlotNumber = 3, MonitorId = "MON2", X = 200, Y = 0, Width = 100, Height = 100 }
            }
        };

        Assert.True(IsMultiMonitorProfile(profile));
    }

    [Fact]
    public void IsMultiMonitorProfile_SingleMonitorId_ReturnsFalse()
    {
        var profile = new LayoutProfile
        {
            IsBuiltIn = false,
            Slots = new ObservableCollection<LayoutSlot>
            {
                new LayoutSlot { SlotNumber = 1, MonitorId = "MON1", X = 0, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { SlotNumber = 2, MonitorId = "MON1", X = 100, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { SlotNumber = 3, MonitorId = "MON1", X = 200, Y = 0, Width = 100, Height = 100 }
            }
        };

        Assert.False(IsMultiMonitorProfile(profile));
    }

    [Fact]
    public void IsMultiMonitorProfile_AllEmptyMonitorIds_ReturnsFalse()
    {
        var profile = new LayoutProfile
        {
            IsBuiltIn = false,
            Slots = new ObservableCollection<LayoutSlot>
            {
                new LayoutSlot { SlotNumber = 1, MonitorId = "", X = 0, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { SlotNumber = 2, MonitorId = "", X = 100, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { SlotNumber = 3, MonitorId = "", X = 200, Y = 0, Width = 100, Height = 100 }
            }
        };

        Assert.False(IsMultiMonitorProfile(profile));
    }

    [Fact]
    public void IsMultiMonitorProfile_BuiltInWithMultipleMonitorIds_ReturnsFalse()
    {
        var profile = new LayoutProfile
        {
            IsBuiltIn = true,
            Slots = new ObservableCollection<LayoutSlot>
            {
                new LayoutSlot { SlotNumber = 1, MonitorId = "MON1", X = 0, Y = 0, Width = 100, Height = 100 },
                new LayoutSlot { SlotNumber = 2, MonitorId = "MON2", X = 100, Y = 0, Width = 100, Height = 100 }
            }
        };

        Assert.False(IsMultiMonitorProfile(profile));
    }

    [Fact]
    public void IsMultiMonitorProfile_EmptyProfile_ReturnsFalse()
    {
        var profile = new LayoutProfile
        {
            IsBuiltIn = false,
            Slots = new ObservableCollection<LayoutSlot>()
        };

        Assert.False(IsMultiMonitorProfile(profile));
    }
}
