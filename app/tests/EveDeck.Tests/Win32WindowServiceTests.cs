using Xunit;
using EveDeck.Services;

namespace EveDeck.Tests;

public class Win32WindowServiceTests
{
    [Fact]
    public void TryFindWindowByProcessName_NoMatchingProcess_ReturnsFalse()
    {
        var service = new Win32WindowService();

        var found = service.TryFindWindowByProcessName("this_process_definitely_does_not_exist_xyz", out var handle, out var rect);

        Assert.False(found);
        Assert.Equal(0, handle);
        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(0, rect.Width);
        Assert.Equal(0, rect.Height);
    }

    [Fact]
    public void IsWindowAlive_ZeroHandle_ReturnsFalse()
    {
        var service = new Win32WindowService();

        Assert.False(service.IsWindowAlive(0));
    }

    [Fact]
    public void IsProcessRunning_NoMatchingProcess_ReturnsFalse()
    {
        var service = new Win32WindowService();

        Assert.False(service.IsProcessRunning("this_process_definitely_does_not_exist_xyz"));
    }
}
