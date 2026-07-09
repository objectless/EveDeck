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

    // Last-write time of the underlying .dat file. The last client closed has the freshest
    // settings, so lists are sorted by this (newest first) and it is shown in the subtitle.
    public DateTime LastWriteUtc { get; init; }

    // For char items: the core_user account id this character was paired with (mtime
    // correlation, optionally overridden by the user). Null when unpaired.
    private string? _accountId;
    public string? AccountId
    {
        get => _accountId;
        set { _accountId = value; OnPropertyChanged(); OnPropertyChanged(nameof(Subtitle)); }
    }

    public string Subtitle
    {
        get
        {
            var time = LastWriteUtc == default ? "" : LastWriteUtc.ToLocalTime().ToString("g");
            if (IsAccount)
                return time.Length > 0 ? $"last saved {time}" : "";
            var acct = _accountId is null ? "account unknown" : $"Account {_accountId}";
            return time.Length > 0 ? $"{CharacterId}  |  {acct}  |  {time}" : $"{CharacterId}  |  {acct}";
        }
    }

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
