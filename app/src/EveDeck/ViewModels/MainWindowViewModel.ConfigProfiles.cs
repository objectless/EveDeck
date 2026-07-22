using System.Collections.ObjectModel;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;

namespace EveDeck.ViewModels;

// Config profiles: one named switch for "Mining" vs "PvP". See Models/ConfigProfile.cs for the shape
// and why the layout/character-set halves are stored as references rather than copies.
//
// Switching is deliberately NOT exposed as a hotkey or an in-app dropdown -- it happens from the tray
// menu and optionally on startup. Creating and editing them lives in Options; those are different
// jobs and mixing a destructive editor into a quick-switch menu is how you rename the wrong profile.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<ConfigProfile> ConfigProfiles => _settings.ConfigProfiles;

    public RelayCommand AddConfigProfileCommand { get; private set; } = null!;
    public RelayCommand RemoveConfigProfileCommand { get; private set; } = null!;
    public RelayCommand SaveCurrentToConfigProfileCommand { get; private set; } = null!;
    public RelayCommand ApplyConfigProfileCommand { get; private set; } = null!;

    // Raised after the active config profile changes so the tray menu can rebuild its checkmarks.
    public event EventHandler? ConfigProfilesChanged;

    private void InitConfigProfiles()
    {
        ConfigProfileService.Log = msg => Log.Warn(msg);
        AddConfigProfileCommand = new RelayCommand(_ => AddConfigProfile());
        RemoveConfigProfileCommand = new RelayCommand(RemoveConfigProfile, p => p is ConfigProfile);
        SaveCurrentToConfigProfileCommand = new RelayCommand(SaveCurrentToConfigProfile, p => p is ConfigProfile);
        ApplyConfigProfileCommand = new RelayCommand(ApplyConfigProfile, p => p is ConfigProfile);
    }

    public bool ApplyConfigProfileOnStartup
    {
        get => _settings.ApplyConfigProfileOnStartup;
        set
        {
            if (_settings.ApplyConfigProfileOnStartup == value) return;
            _settings.ApplyConfigProfileOnStartup = value;
            OnPropertyChanged();
            Save();
        }
    }

    public string ActiveConfigProfileId => _settings.ActiveConfigProfileId;

    // Called once during startup, after profiles/character sets have loaded. No-op unless the user
    // opted in AND the remembered profile still exists.
    internal void ApplyStartupConfigProfile()
    {
        if (!_settings.ApplyConfigProfileOnStartup) return;
        var profile = _settings.ConfigProfiles.FirstOrDefault(p => p.Id == _settings.ActiveConfigProfileId);
        if (profile is null) return;
        Log.Info($"Applying config profile '{profile.Name}' on startup.");
        ApplyConfigProfile(profile);
    }

    private void AddConfigProfile()
    {
        // Seeded from the CURRENT state rather than from defaults: "make a config profile" almost
        // always means "remember how things look right now so I can come back to it".
        var profile = new ConfigProfile
        {
            Name = $"Config {_settings.ConfigProfiles.Count + 1}",
            LayoutProfileId = SelectedProfile?.Id ?? "",
            CharacterSetId = _settings.ActiveCharacterSetId,
        };
        ConfigProfileService.Capture(profile, _settings);
        _settings.ConfigProfiles.Add(profile);
        Save();
        ConfigProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveConfigProfile(object? parameter)
    {
        if (parameter is not ConfigProfile profile) return;

        var result = MessageBox.Show(
            $"Delete config profile '{profile.Name}'?",
            "Delete Config Profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _settings.ConfigProfiles.Remove(profile);
        if (_settings.ActiveConfigProfileId == profile.Id) _settings.ActiveConfigProfileId = "";
        Save();
        OnPropertyChanged(nameof(ActiveConfigProfileId));
        ConfigProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    // Re-snapshots the live state into an existing profile -- the "update this one to match what I
    // have now" action, as opposed to creating another near-duplicate.
    private void SaveCurrentToConfigProfile(object? parameter)
    {
        if (parameter is not ConfigProfile profile) return;
        profile.LayoutProfileId = SelectedProfile?.Id ?? "";
        profile.CharacterSetId = _settings.ActiveCharacterSetId;
        ConfigProfileService.Capture(profile, _settings);
        Save();
        Log.Info($"Saved current setup into config profile '{profile.Name}'.");
    }

    private void ApplyConfigProfile(object? parameter)
    {
        if (parameter is not ConfigProfile profile) return;

        // Each half degrades independently. A config profile pointing at a layout or character set
        // that has since been deleted must still apply everything else rather than throwing -- these
        // are references by design (see ConfigProfile), so dangling ones are expected, not corruption.
        if (!string.IsNullOrWhiteSpace(profile.CharacterSetId))
        {
            if (_settings.CharacterSets.Any(s => s.Id == profile.CharacterSetId))
                SwitchToCharacterSet(profile.CharacterSetId);
            else
                Log.Warn($"Config profile '{profile.Name}' references a character set that no longer exists; keeping the current one.");
        }

        if (!string.IsNullOrWhiteSpace(profile.LayoutProfileId))
        {
            var layout = _settings.Profiles.FirstOrDefault(p => p.Id == profile.LayoutProfileId);
            if (layout is not null) SelectedProfile = layout;
            else Log.Warn($"Config profile '{profile.Name}' references a layout profile that no longer exists; keeping the current one.");
        }

        var applied = ConfigProfileService.ApplyAppearance(profile, _settings);
        if (applied == 0)
            Log.Warn($"Config profile '{profile.Name}' carried no appearance settings; only its layout/character set were applied.");

        _settings.ActiveConfigProfileId = profile.Id;
        Save();

        // The appearance write above went straight onto the settings object, bypassing every
        // view-model property that would normally have raised change notifications and rebuilt the
        // overlay. Refresh the bindings and rebuild the surfaces so the new look actually shows.
        RaiseAllOverlayAppearanceChanged();
        if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
        else if (!_settings.CornerOverlaysEnabled && CornerOverlaysLive) StopCornerOverlays();
        ApplyActiveProfile();

        OnPropertyChanged(nameof(ActiveConfigProfileId));
        ConfigProfilesChanged?.Invoke(this, EventArgs.Empty);
        Log.Info($"Applied config profile '{profile.Name}' ({applied} appearance settings).");
    }

    // Blanket change notification after a config profile rewrites settings underneath the bindings.
    // Deliberately a null (empty-string) PropertyChanged, which WPF treats as "every property on this
    // object changed" -- far more robust than listing ~40 names that would silently rot as settings
    // are added to ConfigProfileService's whitelist.
    private void RaiseAllOverlayAppearanceChanged() => OnPropertyChanged(string.Empty);
}
