using EveDeck.Utilities;

namespace EveDeck.Models;

public sealed class ChatAlertRule : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _keyword = "";
    public string Keyword
    {
        get => _keyword;
        set => SetProperty(ref _keyword, value);
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    // Best-effort scoping: null/empty matches any character's chatlog file. When set, only
    // chatlog filenames containing this text (substring match) are checked against Keyword —
    // consistent with the substring-match convention used elsewhere for window titles.
    private string? _characterName;
    public string? CharacterName
    {
        get => _characterName;
        set => SetProperty(ref _characterName, value);
    }
}
