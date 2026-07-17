using System.IO.Pipes;
using System.Text;
using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

// Exercises the named-pipe protocol end-to-end by playing the role of the native Mumble plugin:
// connect to the service's pipe as a client and stream the same JSON lines the plugin emits.
public class MumbleBridgeServiceTests : IDisposable
{
    // A unique pipe per test instance. The production name is machine-global and single-instance,
    // so sharing it made these tests fail whenever a real EveDeck was running on the dev box (it
    // owns the pipe, the test server can never bind, and every wait burns its full timeout).
    private readonly string _pipeName = $"EveDeckMumble.Test.{Guid.NewGuid():N}";
    private readonly MumbleBridgeService _service;

    public MumbleBridgeServiceTests() => _service = new MumbleBridgeService(_pipeName);

    public void Dispose() => _service.Dispose();

    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
        await client.ConnectAsync(5000);
        return client;
    }

    private static async Task SendAsync(NamedPipeClientStream client, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await client.WriteAsync(bytes);
        await client.FlushAsync();
    }

    // Polls until the condition holds or the timeout elapses -- pipe handling is async, so the
    // roster updates a moment after the bytes are written.
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task SyncTalkJoinLeaveClear_UpdateRoster()
    {
        _service.Start();
        await using var client = await ConnectAsync();
        await WaitForAsync(() => _service.PluginConnected);

        await SendAsync(client,
            """{"e":"sync","channel":"Fleet Ops","users":[{"id":1,"name":"Alpha","state":0},{"id":2,"name":"Bravo","state":0}]}""");
        await WaitForAsync(() => _service.GetSnapshot().Count == 2);
        Assert.Equal("Fleet Ops", _service.ChannelName);

        await SendAsync(client, """{"e":"talk","id":2,"name":"Bravo","state":1}""");
        await WaitForAsync(() => _service.GetSnapshot().SingleOrDefault(t => t.Id == 2)?.State == 1);

        // Stopping talking records activity time for the recently-active display window.
        await SendAsync(client, """{"e":"talk","id":2,"name":"Bravo","state":0}""");
        await WaitForAsync(() => _service.GetSnapshot().Single(t => t.Id == 2).State == 0);
        Assert.True(DateTime.UtcNow - _service.GetSnapshot().Single(t => t.Id == 2).LastActiveUtc
                    < TimeSpan.FromSeconds(5));

        await SendAsync(client, """{"e":"join","id":3,"name":"Charlie"}""");
        await WaitForAsync(() => _service.GetSnapshot().Count == 3);

        await SendAsync(client, """{"e":"leave","id":1}""");
        await WaitForAsync(() => _service.GetSnapshot().All(t => t.Id != 1));

        await SendAsync(client, """{"e":"clear"}""");
        await WaitForAsync(() => _service.GetSnapshot().Count == 0);
        Assert.Equal("", _service.ChannelName);
    }

    [Fact]
    public async Task MalformedLine_IsIgnored_AndSubsequentMessagesStillApply()
    {
        _service.Start();
        await using var client = await ConnectAsync();
        await WaitForAsync(() => _service.PluginConnected);

        await SendAsync(client, "{not json at all");
        await SendAsync(client, """{"e":"join","id":7,"name":"Delta"}""");
        await WaitForAsync(() => _service.GetSnapshot().Count == 1);
        Assert.Equal("Delta", _service.GetSnapshot().Single().Name);
    }

    [Fact]
    public async Task ClientDisconnect_ClearsRoster_AndAllowsReconnect()
    {
        _service.Start();
        var client = await ConnectAsync();
        await WaitForAsync(() => _service.PluginConnected);
        await SendAsync(client, """{"e":"join","id":1,"name":"Alpha"}""");
        await WaitForAsync(() => _service.GetSnapshot().Count == 1);

        await client.DisposeAsync();
        await WaitForAsync(() => !_service.PluginConnected);
        Assert.Empty(_service.GetSnapshot());

        // A fresh plugin session (Mumble restarted) must be able to connect again.
        await using var second = await ConnectAsync();
        await WaitForAsync(() => _service.PluginConnected);
        await SendAsync(second, """{"e":"join","id":9,"name":"Echo"}""");
        await WaitForAsync(() => _service.GetSnapshot().Count == 1);
    }
}
