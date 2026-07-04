using System.Collections.ObjectModel;
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

    private CharacterProfileItem? _sourceProfile;
    public CharacterProfileItem? SourceProfile
    {
        get => _sourceProfile;
        set
        {
            if (SetProperty(ref _sourceProfile, value))
                ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();
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
            () => SourceProfile is not null
                && CharacterProfiles.Any(c => c.IsSelected && c != SourceProfile)
                && !ProfileCopyInProgress);

        SelectAllProfileTargetsCommand = new RelayCommand(() =>
        {
            foreach (var c in CharacterProfiles)
                if (c != SourceProfile) c.IsSelected = true;
            ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();
        });

        ClearAllProfileTargetsCommand = new RelayCommand(() =>
        {
            foreach (var c in CharacterProfiles) c.IsSelected = false;
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
        ProfileCopyStatus = "Loading characters...";
        ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();

        var files = _eveSettings.GetCharacterFiles(folderPath);
        var items = new List<CharacterProfileItem>();

        foreach (var file in files)
        {
            var id = EveSettingsService.GetCharacterId(file);
            if (id is null) continue;
            var item = new CharacterProfileItem { FilePath = file, CharacterId = id };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CharacterProfileItem.IsSelected))
                    ExecuteCopyProfilesCommand.RaiseCanExecuteChanged();
            };
            items.Add(item);
            CharacterProfiles.Add(item);
        }

        ProfileCopyStatus = $"Found {items.Count} character file(s). Resolving names via ESI...";

        await Task.WhenAll(items.Select(ResolveNameAsync));

        ProfileCopyStatus = $"Ready — {items.Count} character(s) loaded. Pick a source on the left, check targets on the right.";
    }

    private async Task ResolveNameAsync(CharacterProfileItem item)
    {
        var name = await _eveSettings.ResolveCharacterNameAsync(item.CharacterId);
        if (name is not null)
            System.Windows.Application.Current.Dispatcher.Invoke(() => item.CharacterName = name);
    }

    private async Task CopyProfilesAsync()
    {
        if (SourceProfile is null) return;
        var targets = CharacterProfiles.Where(c => c.IsSelected && c != SourceProfile).ToList();
        if (targets.Count == 0) return;

        ProfileCopyInProgress = true;
        ProfileCopyStatus = $"Copying to {targets.Count} character(s)...";

        var errors = new List<string>();
        await Task.Run(() =>
        {
            foreach (var target in targets)
            {
                var err = _eveSettings.CopyProfile(SourceProfile.FilePath, target.FilePath);
                if (err is not null)
                    errors.Add($"{target.DisplayName}: {err}");
            }
        });

        ProfileCopyInProgress = false;

        if (errors.Count == 0)
            ProfileCopyStatus = $"Done! Copied {SourceProfile.DisplayName}'s settings to {targets.Count} character(s). Originals backed up.";
        else
            ProfileCopyStatus = $"Completed with {errors.Count} error(s): {string.Join("; ", errors)}";

        Log.Info(ProfileCopyStatus);
    }
}
