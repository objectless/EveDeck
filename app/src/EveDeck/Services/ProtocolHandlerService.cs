using System.IO;
using System.IO.Pipes;

namespace EveDeck.Services;

// evedeck:// URL protocol plumbing: per-user registry registration (HKCU — no admin rights), a
// named-pipe listener in the running instance, and client-side forwarding for a second instance
// launched by the OS to service a URL. Commands are parsed and dispatched by the view-model
// (HandleProtocolUrl), which validates every verb through SafetyGuard — this class only moves the
// URL string; it never touches EVE windows itself.
//
// Use cases: Stream Deck / macro-pad buttons ("evedeck://center/Scout Name"), links in intel
// tools, or shell shortcuts — programmatic access to the SAME window-management actions the UI
// and hotkeys offer, nothing more.
public sealed class ProtocolHandlerService : IDisposable
{
    public const string Scheme = "evedeck";
    private const string PipeName = "EveDeck.Protocol";

    private CancellationTokenSource? _cts;

    // Create/refresh the HKCU class registration so the OS launches this exe for evedeck:// links.
    // Idempotent; re-run on every startup so the command path self-heals after the app moves
    // (portable folder rename, Velopack update swapping install dirs, ...).
    public static void RegisterUrlProtocol()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
        root.SetValue("", "URL:EveDeck Protocol");
        root.SetValue("URL Protocol", "");
        using var icon = root.CreateSubKey("DefaultIcon");
        icon.SetValue("", $"\"{exe}\",0");
        using var command = root.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{exe}\" \"%1\"");
    }

    // Listen for URLs forwarded by secondary instances. onUrl is invoked on a background thread —
    // the subscriber is responsible for marshalling to the UI thread.
    public void StartServer(Action<string> onUrl)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    var url = await reader.ReadLineAsync(token);
                    if (!string.IsNullOrWhiteSpace(url)) onUrl(url);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // Pipe hiccup (client vanished mid-write, ...) — recreate the server and keep listening.
                }
            }
        }, token);
    }

    // Called by a SECOND instance that the OS started to service an evedeck:// link while the app
    // is already running: hand the URL to the live instance's pipe and report success.
    public static bool TryForwardToRunningInstance(string url)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(url);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
