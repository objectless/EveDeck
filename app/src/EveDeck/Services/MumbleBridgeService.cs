using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace EveDeck.Services;

// One user's presence as reported by the EveDeck Mumble plugin. State mirrors Mumble's
// Mumble_TalkingState: 0 passive, 1 talking, 2 whispering, 3 shouting, 4 talking-muted.
public sealed class MumbleTalker
{
    public uint Id { get; init; }
    public string Name { get; set; } = "?";
    public int State { get; set; }
    // Last time this user transitioned out of a talking state; used for the
    // "recently active" display window after they stop talking.
    public DateTime LastActiveUtc { get; set; }
}

// Hosts the \\.\pipe\EveDeckMumble named-pipe server that the EveDeck Mumble plugin (a tiny
// native plugin loaded by the user's own Mumble client) connects to, and maintains the current
// channel roster + talking states from its JSON-line events. Read-only presence data only --
// no audio, chat, or credentials ever cross this pipe.
public sealed class MumbleBridgeService : IDisposable
{
    private const string PipeName = "EveDeckMumble";

    private readonly object _lock = new();
    private readonly Dictionary<uint, MumbleTalker> _talkers = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public string ChannelName { get; private set; } = "";
    public bool PluginConnected { get; private set; }

    // Raised (on a background thread) whenever the roster, a talking state, or the plugin's
    // connection status changes. Subscribers must marshal to the UI thread themselves.
    public event Action? Changed;

    // Wired by the ViewModel to its LogService (services in this app don't own a logger).
    public Action<string>? OnLog { get; set; }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;
        cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* cancellation */ }
        cts.Dispose();
        lock (_lock) _talkers.Clear();
        PluginConnected = false;
    }

    public IReadOnlyList<MumbleTalker> GetSnapshot()
    {
        lock (_lock)
            return [.. _talkers.Values.Select(t => new MumbleTalker
            {
                Id = t.Id, Name = t.Name, State = t.State, LastActiveUtc = t.LastActiveUtc,
            })];
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);

                PluginConnected = true;
                Changed?.Invoke();
                OnLog?.Invoke("Mumble plugin connected to the EveDeck bridge pipe.");

                using var reader = new StreamReader(server, System.Text.Encoding.UTF8);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break; // plugin disconnected (Mumble closed / plugin disabled)
                    if (line.Length == 0) continue;
                    try { HandleMessage(line); }
                    catch (Exception ex) { OnLog?.Invoke($"Mumble bridge: bad message ignored ({ex.Message})."); }
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Mumble bridge pipe error: {ex.Message}");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (PluginConnected)
                {
                    PluginConnected = false;
                    lock (_lock) _talkers.Clear();
                    Changed?.Invoke();
                    OnLog?.Invoke("Mumble plugin disconnected from the EveDeck bridge pipe.");
                }
            }
        }
    }

    private void HandleMessage(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var evt = root.GetProperty("e").GetString();

        lock (_lock)
        {
            switch (evt)
            {
                case "sync":
                    _talkers.Clear();
                    ChannelName = root.TryGetProperty("channel", out var ch) ? ch.GetString() ?? "" : "";
                    if (root.TryGetProperty("users", out var users))
                        foreach (var u in users.EnumerateArray())
                        {
                            var id = u.GetProperty("id").GetUInt32();
                            _talkers[id] = new MumbleTalker
                            {
                                Id = id,
                                Name = u.GetProperty("name").GetString() ?? "?",
                                State = u.TryGetProperty("state", out var st) ? st.GetInt32() : 0,
                            };
                        }
                    break;

                case "talk":
                {
                    var id = root.GetProperty("id").GetUInt32();
                    var state = root.GetProperty("state").GetInt32();
                    if (!_talkers.TryGetValue(id, out var talker))
                        _talkers[id] = talker = new MumbleTalker { Id = id };
                    if (root.TryGetProperty("name", out var name))
                        talker.Name = name.GetString() ?? talker.Name;
                    // Any transition through a talking state counts as activity, so a user who
                    // just stopped keeps showing in the "recently active" window.
                    if (talker.State > 0 || state > 0)
                        talker.LastActiveUtc = DateTime.UtcNow;
                    talker.State = state;
                    break;
                }

                case "join":
                {
                    var id = root.GetProperty("id").GetUInt32();
                    _talkers[id] = new MumbleTalker
                    {
                        Id = id,
                        Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? "?" : "?",
                    };
                    break;
                }

                case "leave":
                    _talkers.Remove(root.GetProperty("id").GetUInt32());
                    break;

                case "clear":
                    _talkers.Clear();
                    ChannelName = "";
                    break;
            }
        }
    }
}
