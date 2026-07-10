using EveDeck.Utilities;

namespace EveDeck.Models;

// One structured game-event alert: a substring matched against new lines in EVE's own
// Gamelogs files (Documents\EVE\logs\Gamelogs). Same passive log-tailing model as
// ChatAlertRule -- plain file I/O over logs EVE writes itself, never game input.
public sealed class GameEventRule : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // Display name shown in Options and in alert log lines (e.g. "Combat", "Fleet invite").
    private string _name = "";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    // Case-insensitive substring matched against each new gamelog line. Editable because CCP
    // wording can change between patches and localised clients log localised text.
    private string _pattern = "";
    public string Pattern
    {
        get => _pattern;
        set => SetProperty(ref _pattern, value);
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    private bool _playSound = true;
    public bool PlaySound
    {
        get => _playSound;
        set => SetProperty(ref _playSound, value);
    }

    // Skip the alert when the matching character's own window is foreground -- you can already
    // see that client, so e.g. combat on the ACTIVE window shouldn't chime.
    private bool _suppressWhenFocused = true;
    public bool SuppressWhenFocused
    {
        get => _suppressWhenFocused;
        set => SetProperty(ref _suppressWhenFocused, value);
    }

    public static IEnumerable<GameEventRule> Defaults() => new[]
    {
        new GameEventRule { Name = "Combat",            Pattern = "(combat)" },
        new GameEventRule { Name = "Asteroid depleted", Pattern = "depleted", PlaySound = false },
        new GameEventRule { Name = "Mining crystal",    Pattern = "crystal",  PlaySound = false },
        new GameEventRule { Name = "Fleet invite",      Pattern = "join their fleet", SuppressWhenFocused = false },
        new GameEventRule { Name = "Conversation",      Pattern = "wants to talk", SuppressWhenFocused = false },
    };
}
