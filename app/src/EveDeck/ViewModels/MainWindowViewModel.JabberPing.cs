using System.Diagnostics;
using System.Media;
using EveDeck.Services;
using Application = System.Windows.Application;

namespace EveDeck.ViewModels;

// Jabber ping bridge: reads Pidgin's own HTML conversation logs (JabberPingWatcherService) for a
// user-designated conversation and raises a toast per message -- no XMPP login of its own, no
// second identity in the room. Deliberately the log-file-reading sibling of the EVE chat/game log
// watchers, not a native protocol client; see JabberPingWatcherService's own doc comment for why.
public sealed partial class MainWindowViewModel
{
    public bool JabberPingEnabled
    {
        get => _settings.JabberPingEnabled;
        set
        {
            if (_settings.JabberPingEnabled == value) return;
            _settings.JabberPingEnabled = value;
            OnPropertyChanged();
            Save();
        }
    }

    public string JabberPingConversationName
    {
        get => _settings.JabberPingConversationName;
        set
        {
            if (_settings.JabberPingConversationName == value) return;
            _settings.JabberPingConversationName = value;
            OnPropertyChanged();
            Save();
        }
    }

    public string JabberPingRequiredPhrase
    {
        get => _settings.JabberPingRequiredPhrase;
        set
        {
            if (_settings.JabberPingRequiredPhrase == value) return;
            _settings.JabberPingRequiredPhrase = value;
            OnPropertyChanged();
            Save();
        }
    }

    public string MumbleServerHost
    {
        get => _settings.MumbleServerHost;
        set
        {
            if (_settings.MumbleServerHost == value) return;
            _settings.MumbleServerHost = value;
            OnPropertyChanged();
            Save();
        }
    }

    private string _jabberBridgeStatus = "Not started.";
    public string JabberBridgeStatus
    {
        get => _jabberBridgeStatus;
        private set => SetProperty(ref _jabberBridgeStatus, value);
    }

    private void InitJabberPing()
    {
        _jabberPingWatcherService.ConversationNameFilterProvider = () => _settings.JabberPingConversationName;
        _jabberPingWatcherService.ErrorOccurred += msg =>
        {
            Log.Warn(msg);
            Application.Current?.Dispatcher.BeginInvoke(() => JabberBridgeStatus = msg);
        };
        _jabberPingWatcherService.MessageReceived += (sender, message) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => OnJabberMessageReceived(sender, message));
        };
        _jabberPingWatcherService.Start();
        JabberBridgeStatus = _jabberPingWatcherService.IsRunning
            ? "Running -- watching Pidgin's log folder for new messages."
            : "Not running -- see the warning above (if any) for why.";
    }

    private void OnJabberMessageReceived(string sender, string message)
    {
        if (!_settings.JabberPingEnabled) return;

        var requiredPhrase = _settings.JabberPingRequiredPhrase;
        if (!string.IsNullOrWhiteSpace(requiredPhrase)
            && message.IndexOf(requiredPhrase, StringComparison.OrdinalIgnoreCase) < 0)
            return;

        Log.Info($"Jabber ping from {sender}: {message}");
        SystemSounds.Exclamation.Play();

        var mumbleUrl = BuildMumbleJoinUrl(message);
        if (mumbleUrl is null)
        {
            ShowToast(sender, message, "#0EA5E9");
            return;
        }

        var body = $"{message}\n\n🎙 Click to join comms";
        void OpenMumble()
        {
            try { Process.Start(new ProcessStartInfo(mumbleUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn($"Could not open Mumble join link: {ex.Message}"); }
        }
        ShowToast(sender, body, "#0EA5E9", (Action)OpenMumble, mumbleUrl);
    }

    // Builds a mumble://host/channel join link from a ping's "Comms:" field, when both a server host
    // is configured (Options > Comms) and the ping actually named a channel. Null (no link, plain
    // toast) is the correct, safe default whenever either piece is missing -- EveDeck has no way to
    // know the user's Mumble server address on its own.
    private string? BuildMumbleJoinUrl(string message)
    {
        var host = _settings.MumbleServerHost;
        if (string.IsNullOrWhiteSpace(host)) return null;

        var channel = JabberPingWatcherService.TryExtractCommsChannel(message);
        if (string.IsNullOrWhiteSpace(channel)) return null;

        var segments = channel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        return $"mumble://{host.Trim()}/{string.Join("/", segments)}";
    }
}
