using System.Collections.ObjectModel;
using System.IO;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;

namespace EveDeck.ViewModels;

public record ProfileFolderOption(string Path, string DisplayName);

public sealed partial class MainWindowViewModel
{
    private readonly EveSettingsService _eveSettings = new();

    private ObservableCollection<ProfileFolderOption> _settingsFolders = new();
    public ObservableCollection<ProfileFolderOption> SettingsFolders
    {
        get => _settingsFolders;
        private set { _settingsFolders = value; OnPropertyChanged(); }
    }

    private ProfileFolderOption? _selectedSettingsFolder;
    public ProfileFolderOption? SelectedSettingsFolder
    {
        get => _selectedSettingsFolder;
        set
        {
            if (SetProperty(ref _selectedSettingsFolder, value) && value is not null)
                _ = LoadCharacterProfilesAsync(value.Path);
        }
    }

    private ObservableCollection<CharacterProfileItem> _characterProfiles = new();
    public ObservableCollection<CharacterProfileItem> CharacterProfiles
    {
        get => _characterProfiles;
        private set { _characterProfiles = value; OnPropertyChanged(); }
    }

    // Reentrancy guard for the char <-> account selection sync below.
    private bool _syncingProfileSelection;

    private CharacterProfileItem? _sourceProfile;
    public CharacterProfileItem? SourceProfile
    {
        get => _sourceProfile;
        set
        {
            if (SetProperty(ref _sourceProfile, value))
            {
                // Picking a source character auto-selects its paired account file.
                if (!_syncingProfileSelection && value?.AccountId is string acct)
                {
                    var match = AccountProfiles.FirstOrDefault(a => a.CharacterId == acct);
                    if (match is not null)
                    {
                        _syncingProfileSelection = true;
                        SourceAccountProfile = match;
                        _syncingProfileSelection = false;
                    }
                }
                ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // Per-account core_user files, synced alongside the character files so the copy is truly 1:1 --
    // window positions and other account-scoped UI state live in core_user, not core_char.
    private ObservableCollection<CharacterProfileItem> _accountProfiles = new();
    public ObservableCollection<CharacterProfileItem> AccountProfiles
    {
        get => _accountProfiles;
        private set { _accountProfiles = value; OnPropertyChanged(); }
    }

    private CharacterProfileItem? _sourceAccountProfile;
    public CharacterProfileItem? SourceAccountProfile
    {
        get => _sourceAccountProfile;
        set
        {
            if (SetProperty(ref _sourceAccountProfile, value))
            {
                // A manual account pick while a source character is selected corrects that
                // character's pairing; persist it so it survives restarts and stale mtimes.
                if (!_syncingProfileSelection && value is not null && _sourceProfile is not null
                    && _sourceProfile.AccountId != value.CharacterId)
                {
                    _sourceProfile.AccountId = value.CharacterId;
                    _settings.ProfileCharAccountOverrides[_sourceProfile.CharacterId] = value.CharacterId;
                    Save();
                }
                ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _profileCopyInProgress;
    public bool ProfileCopyInProgress
    {
        get => _profileCopyInProgress;
        private set
        {
            if (SetProperty(ref _profileCopyInProgress, value))
                ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();
        }
    }

    private string _profileCopyStatus = "Select a settings folder to load characters.";
    public string ProfileCopyStatus
    {
        get => _profileCopyStatus;
        private set => SetProperty(ref _profileCopyStatus, value);
    }

    public RelayCommand RefreshSettingsFoldersCommand { get; private set; } = null!;
    public RelayCommand ExecuteCopyProfilesCommand { get; private set; } = null!;
    public RelayCommand SelectAllProfileTargetsCommand { get; private set; } = null!;
    public RelayCommand ClearAllProfileTargetsCommand { get; private set; } = null!;

    partial void InitProfileCopy()
    {
        RefreshSettingsFoldersCommand = new RelayCommand(DiscoverSettingsFolders);

        ExecuteCopyProfilesCommand = new RelayCommand(
            async () => await CopyProfilesAsync(),
            () => !ProfileCopyInProgress
                && ((SourceProfile is not null
                        && CharacterProfiles.Any(c => c.IsSelected && c != SourceProfile))
                    || (SourceAccountProfile is not null
                        && AccountProfiles.Any(a => a.IsSelected && a != SourceAccountProfile))));

        SelectAllProfileTargetsCommand = new RelayCommand(() =>
        {
            foreach (var c in CharacterProfiles)
                if (c != SourceProfile) c.IsSelected = true;
            foreach (var a in AccountProfiles)
                if (a != SourceAccountProfile) a.IsSelected = true;
            ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();
        });

        ClearAllProfileTargetsCommand = new RelayCommand(() =>
        {
            foreach (var c in CharacterProfiles) c.IsSelected = false;
            foreach (var a in AccountProfiles) a.IsSelected = false;
            ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();
        });

        DiscoverSettingsFolders();
    }

    private void DiscoverSettingsFolders()
    {
        var folders = _eveSettings.FindSettingsFolders();
        SettingsFolders.Clear();
        foreach (var path in folders)
            SettingsFolders.Add(new ProfileFolderOption(path, EveSettingsService.GetFolderDisplayName(path)));

        if (SettingsFolders.Count > 0 && _selectedSettingsFolder is null)
            SelectedSettingsFolder = SettingsFolders[0];

        ProfileCopyStatus = folders.Count > 0
            ? $"Found {folders.Count} settings folder(s)."
            : "No EVE settings folders found under %LOCALAPPDATA%\\CCP\\EVE.";
    }

    private async Task LoadCharacterProfilesAsync(string folderPath)
    {
        CharacterProfiles.Clear();
        SourceProfile = null;
        AccountProfiles.Clear();
        SourceAccountProfile = null;
        ProfileCopyStatus = "Loading characters...";
        ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();

        var userFiles = _eveSettings.GetUserFiles(folderPath);
        var files = _eveSettings.GetCharacterFiles(folderPath);

        // Accounts aren't exposed by ESI, so char -> account pairing comes from file-mtime
        // correlation (EVE writes both files together on logout), with persisted manual
        // overrides taking precedence.
        var pairs = EveSettingsService.PairCharactersToAccounts(files, userFiles);

        foreach (var userFile in userFiles.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var userId = EveSettingsService.GetUserId(userFile);
            if (userId is null) continue;
            var account = new CharacterProfileItem
            {
                FilePath = userFile,
                CharacterId = userId,
                IsAccount = true,
                LastWriteUtc = File.GetLastWriteTimeUtc(userFile),
            };
            account.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CharacterProfileItem.IsSelected))
                    ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();
            };
            AccountProfiles.Add(account);
        }

        var accountIds = AccountProfiles.Select(a => a.CharacterId).ToHashSet();
        var items = new List<CharacterProfileItem>();

        foreach (var file in files.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var id = EveSettingsService.GetCharacterId(file);
            if (id is null) continue;
            var item = new CharacterProfileItem
            {
                FilePath = file,
                CharacterId = id,
                LastWriteUtc = File.GetLastWriteTimeUtc(file),
            };
            if (_settings.ProfileCharAccountOverrides.TryGetValue(id, out var overrideAcct)
                && accountIds.Contains(overrideAcct))
                item.AccountId = overrideAcct;
            else if (pairs.TryGetValue(id, out var pairedAcct))
                item.AccountId = pairedAcct;
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CharacterProfileItem.IsSelected))
                {
                    SyncTargetAccountSelection(item);
                    ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();
                }
            };
            items.Add(item);
            CharacterProfiles.Add(item);
        }

        ProfileCopyStatus = $"Found {items.Count} character file(s). Resolving names via ESI...";

        await Task.WhenAll(items.Select(ResolveNameAsync));

        ProfileCopyStatus = $"Ready - {items.Count} character(s) loaded, sorted newest first. "
            + "The last client you closed has the freshest settings - pick it as the source.";
    }

    // Checking a target character also checks its paired account file; unchecking releases the
    // account only when no other checked character maps to it.
    private void SyncTargetAccountSelection(CharacterProfileItem charItem)
    {
        if (_syncingProfileSelection || charItem.AccountId is null) return;
        var account = AccountProfiles.FirstOrDefault(a => a.CharacterId == charItem.AccountId);
        if (account is null) return;

        _syncingProfileSelection = true;
        if (charItem.IsSelected)
            account.IsSelected = true;
        else if (!CharacterProfiles.Any(c => c.IsSelected && c.AccountId == charItem.AccountId))
            account.IsSelected = false;
        _syncingProfileSelection = false;
    }

    private async Task ResolveNameAsync(CharacterProfileItem item)
    {
        var name = await _eveSettings.ResolveCharacterNameAsync(item.CharacterId);
        if (name is not null)
            System.Windows.Application.Current.Dispatcher.Invoke(() => item.CharacterName = name);
    }

    private async Task CopyProfilesAsync()
    {
        var charSource = SourceProfile;
        List<CharacterProfileItem> charTargets = charSource is null
            ? []
            : CharacterProfiles.Where(c => c.IsSelected && c != charSource).ToList();
        var accountSource = SourceAccountProfile;
        List<CharacterProfileItem> accountTargets = accountSource is null
            ? []
            : AccountProfiles.Where(a => a.IsSelected && a != accountSource).ToList();
        if (charTargets.Count == 0 && accountTargets.Count == 0) return;

        ProfileCopyInProgress = true;
        ProfileCopyStatus = $"Copying to {charTargets.Count} character(s) and {accountTargets.Count} account(s)...";

        var errors = new List<string>();
        await Task.Run(() =>
        {
            foreach (var target in charTargets)
            {
                var err = _eveSettings.CopyProfile(charSource!.FilePath, target.FilePath);
                if (err is not null)
                    errors.Add($"{target.DisplayName}: {err}");
            }
            foreach (var target in accountTargets)
            {
                var err = _eveSettings.CopyProfile(accountSource!.FilePath, target.FilePath);
                if (err is not null)
                    errors.Add($"{target.DisplayName}: {err}");
            }
        });

        ProfileCopyInProgress = false;

        if (errors.Count == 0)
        {
            var parts = new List<string>();
            if (charTargets.Count > 0) parts.Add($"{charSource!.DisplayName}'s settings to {charTargets.Count} character(s)");
            if (accountTargets.Count > 0) parts.Add($"{accountSource!.DisplayName}'s account settings to {accountTargets.Count} account(s)");
            ProfileCopyStatus = $"Done! Copied {string.Join(" and ", parts)}. Originals backed up.";
        }
        else
            ProfileCopyStatus = $"Completed with {errors.Count} error(s): {string.Join("; ", errors)}";

        Log.Info(ProfileCopyStatus);
    }
}
