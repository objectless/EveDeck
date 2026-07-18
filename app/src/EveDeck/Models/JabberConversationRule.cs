using EveDeck.Utilities;

namespace EveDeck.Models;

// One locally-logged Pidgin conversation folder the user has opted into (or not) for the Jabber
// ping bridge, discovered via JabberPingWatcherService.DiscoverConversations() rather than typed by
// hand -- see MainWindowViewModel.JabberPing.cs's DiscoverJabberConversations/RefreshJabberConversationsCommand.
// Mirrors IntelChannelRule's per-item Enabled shape for the same reason: several ping bots/rooms can
// each be independently opted into rather than one hardcoded conversation.
public sealed class JabberConversationRule : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _conversationName = "";
    public string ConversationName
    {
        get => _conversationName;
        set => SetProperty(ref _conversationName, value);
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    // Optional per-conversation substring filter -- lets a bot's real broadcast pings be told apart
    // from other chatter/self-status noise in the same conversation without EveDeck hardcoding any
    // one ping tool's format. Empty = alert on every message in this conversation.
    private string _requiredPhrase = "";
    public string RequiredPhrase
    {
        get => _requiredPhrase;
        set => SetProperty(ref _requiredPhrase, value);
    }
}
