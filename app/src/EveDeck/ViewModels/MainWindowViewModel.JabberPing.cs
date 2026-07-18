using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Media;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;
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

    // Every locally-discovered Pidgin conversation folder the user has reviewed, each independently
    // enabled/disabled with its own required-phrase filter -- see JabberPingWatcherService.
    // DiscoverConversations() and DiscoverJabberConversations below.
    public ObservableCollection<JabberConversationRule> JabberConversationRules => _settings.JabberConversationRules;

    public RelayCommand RemoveJabberConversationRuleCommand { get; private set; } = null!;

    private string _jabberBridgeStatus = "Not started.";
    public string JabberBridgeStatus
    {
        get => _jabberBridgeStatus;
        private set => SetProperty(ref _jabberBridgeStatus, value);
    }

    public RelayCommand RefreshJabberConversationsCommand { get; private set; } = null!;

    private void InitJabberPing()
    {
        RefreshJabberConversationsCommand = new RelayCommand(DiscoverJabberConversations);
        RemoveJabberConversationRuleCommand = new RelayCommand(parameter =>
        {
            if (parameter is not JabberConversationRule rule) return;
            _settings.JabberConversationRules.Remove(rule);
            Save();
        }, parameter => parameter is JabberConversationRule);

        // One-time migration: the old single-conversation setting becomes the first (enabled) entry
        // in the new per-conversation list, so upgrading users don't lose their configured
        // conversation -- see IntelChannelRules' identical migration in MainWindowViewModel.IntelJumpAlert.cs.
        if (_settings.JabberConversationRules.Count == 0 && !string.IsNullOrWhiteSpace(_settings.JabberPingConversationName))
        {
            _settings.JabberConversationRules.Add(new JabberConversationRule
            {
                ConversationName = _settings.JabberPingConversationName,
                RequiredPhrase = _settings.JabberPingRequiredPhrase,
                Enabled = true,
            });
            Save();
        }

        _jabberPingWatcherService.ErrorOccurred += msg =>
        {
            Log.Warn(msg);
            Application.Current?.Dispatcher.BeginInvoke(() => JabberBridgeStatus = msg);
        };
        _jabberPingWatcherService.MessageReceived += (conversationFolder, sender, message) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => OnJabberMessageReceived(conversationFolder, sender, message));
        };
        _jabberPingWatcherService.Start();
        JabberBridgeStatus = _jabberPingWatcherService.IsRunning
            ? "Running -- watching Pidgin's log folder for new messages."
            : "Not running -- see the warning above (if any) for why.";

        DiscoverJabberConversations();
    }

    // Adds any newly-discovered Pidgin conversation folder as a new (disabled) JabberConversationRule
    // -- never touches or removes existing entries, so per-conversation Enabled/RequiredPhrase choices
    // survive a refresh. New discoveries start disabled, same "don't guess" convention as
    // DiscoverIntelChannels.
    private void DiscoverJabberConversations()
    {
        var found = _jabberPingWatcherService.DiscoverConversations();
        var existing = new HashSet<string>(
            _settings.JabberConversationRules.Select(r => r.ConversationName), StringComparer.OrdinalIgnoreCase);
        foreach (var name in found)
        {
            if (existing.Contains(name)) continue;
            _settings.JabberConversationRules.Add(new JabberConversationRule { ConversationName = name, Enabled = false });
        }
        Save();
    }

    private void OnJabberMessageReceived(string conversationFolder, string sender, string message)
    {
        if (!_settings.JabberPingEnabled) return;

        // First enabled rule whose conversation name is a substring match against the folder this
        // message actually came from wins -- same substring convention used everywhere else in this
        // app (ChatAlertRule, IntelChannelRule).
        var matchedRule = _settings.JabberConversationRules.FirstOrDefault(r =>
            r.Enabled && conversationFolder.IndexOf(r.ConversationName, StringComparison.OrdinalIgnoreCase) >= 0);
        if (matchedRule is null) return;

        if (!string.IsNullOrWhiteSpace(matchedRule.RequiredPhrase)
            && message.IndexOf(matchedRule.RequiredPhrase, StringComparison.OrdinalIgnoreCase) < 0)
            return;

        Log.Info($"Jabber ping from {sender}: {message}");
        SystemSounds.Exclamation.Play();

        var commsUrl = BuildCommsJoinUrl(message);
        if (commsUrl is null)
        {
            ShowToast(sender, message, "#0EA5E9");
            return;
        }

        var body = $"{message}\n\n🎙 Click to join comms";
        void OpenComms()
        {
            try { Process.Start(new ProcessStartInfo(commsUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn($"Could not open comms link: {ex.Message}"); }
        }
        ShowToast(sender, body, "#0EA5E9", (Action)OpenComms, commsUrl);
    }

    // Builds the toast's click-to-join link from a ping's "Comms:" field: the plain link found
    // embedded in that field, if any (see JabberPingWatcherService.TryExtractEmbeddedLink -- real
    // ping tools' Comms fields carry a shortened link like gnf.lt alongside/instead of a bare
    // channel name in practice, which is what's actually clickable). A bare channel name with no
    // link (null here) can't be turned into anything without a Mumble server address to build a
    // mumble://host/channel URI from, and this app deliberately doesn't ask for one anymore -- the
    // embedded link covers the real-world case, and guessing a server address was never reliable.
    private string? BuildCommsJoinUrl(string message)
    {
        var channel = JabberPingWatcherService.TryExtractCommsChannel(message);
        if (string.IsNullOrWhiteSpace(channel)) return null;

        return JabberPingWatcherService.TryExtractEmbeddedLink(channel);
    }
}
