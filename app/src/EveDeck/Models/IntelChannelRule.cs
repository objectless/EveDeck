using EveDeck.Utilities;

namespace EveDeck.Models;

// Windows' own built-in system sounds -- no new audio assets needed, consistent with how sound
// already works everywhere else in this app (SystemSounds.Exclamation.Play(), hardcoded, before
// this per-channel choice existed).
public enum IntelAlertSound { Asterisk, Exclamation, Hand, Question, Beep }

// One locally-logged EVE chatlog channel the user has opted into (or not) for intel jump-distance
// alerts, discovered via ChatLogWatcherService.DiscoverChannels() rather than typed by hand -- see
// MainWindowViewModel.IntelJumpAlert.cs's DiscoverIntelChannels/RefreshIntelChannelsCommand.
public sealed class IntelChannelRule : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _channelName = "";
    public string ChannelName
    {
        get => _channelName;
        set => SetProperty(ref _channelName, value);
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    private IntelAlertSound _sound = IntelAlertSound.Exclamation;
    public IntelAlertSound Sound
    {
        get => _sound;
        set => SetProperty(ref _sound, value);
    }
}
