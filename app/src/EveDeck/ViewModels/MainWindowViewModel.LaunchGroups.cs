using System.Windows;
using MessageBox = System.Windows.MessageBox;
using EveDeck.Models;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    // Path override for the EVE Launcher executable, used when it isn't found at either common
    // install location. Empty string clears the override.
    public string EveLauncherPathOverride
    {
        get => _settings.EveLauncherPathOverride ?? "";
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (_settings.EveLauncherPathOverride == normalized) return;
            _settings.EveLauncherPathOverride = normalized;
            OnPropertyChanged();
            Save();
        }
    }

    private void InitLaunchGroups()
    {
        _clientLaunchService.StatusChanged += msg =>
        {
            Status = msg;
            Log.Info(msg);
        };
        _clientLaunchService.ErrorOccurred += msg =>
        {
            Status = msg;
            Log.Error(msg);
            MessageBox.Show(msg, "Launch Group", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
    }

    private async void LaunchGroup(object? parameter)
    {
        if (parameter is not CharacterSet group) return;

        _launchGroupCts?.Cancel();
        _launchGroupCts = new CancellationTokenSource();
        try
        {
            await _clientLaunchService.LaunchGroupAsync(group, _settings.EveLauncherPathOverride, _launchGroupCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer launch request — expected, nothing to report.
        }
    }
}
