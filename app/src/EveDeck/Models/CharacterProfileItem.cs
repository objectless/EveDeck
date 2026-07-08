using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EveDeck.Models;

public sealed class CharacterProfileItem : INotifyPropertyChanged
{
    public required string FilePath { get; init; }
    public required string CharacterId { get; init; }

    // True for per-account core_user files (account IDs have no ESI name or portrait; they render
    // as "Account <id>"). False for per-character core_char files.
    public bool IsAccount { get; init; }

    private string _characterName = "";
    public string CharacterName
    {
        get => _characterName;
        set { _characterName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string PortraitUrl => !IsAccount && long.TryParse(CharacterId, out var id) && id > 0
        ? $"https://images.evetech.net/characters/{id}/portrait?size=64"
        : "";

    public string DisplayName => IsAccount
        ? $"Account {CharacterId}"
        : string.IsNullOrEmpty(_characterName) ? $"ID {CharacterId}" : _characterName;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
