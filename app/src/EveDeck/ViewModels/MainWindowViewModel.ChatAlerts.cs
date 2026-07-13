using System.Collections.ObjectModel;
using System.Media;
using System.Windows.Threading;
using EveDeck.Models;
using Application = System.Windows.Application;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    public ObservableCollection<ChatAlertRule> ChatAlertRules => _settings.ChatAlertRules;
    public ObservableCollection<GameEventRule> GameEventRules => _settings.GameEventRules;

    public bool AbyssModeEnabled
    {
        get => _settings.AbyssModeEnabled;
        set
        {
            if (_settings.AbyssModeEnabled == value) return;
            _settings.AbyssModeEnabled = value;
            OnPropertyChanged();
            Save();
        }
    }

    // Current solar system per character name (from Local chatlog "Channel changed to Local"
    // lines). Character names come from EVE's own logs, so exact-name matching is reliable.
    private readonly Dictionary<string, string> _systemByCharacter = new(StringComparer.OrdinalIgnoreCase);

    private void InitChatAlerts()
    {
        AddChatAlertRuleCommand.RaiseCanExecuteChanged();

        _chatLogWatcherService.RulesProvider = () => _settings.ChatAlertRules;
        _chatLogWatcherService.ErrorOccurred += msg => Log.Warn(msg);
        _chatLogWatcherService.KeywordMatched += (rule, channel) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => OnChatKeywordMatched(rule, channel));
        };
        _chatLogWatcherService.SystemChanged += (character, system) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => OnCharacterSystemChanged(character, system));
        };
        _chatLogWatcherService.Start();

        _gameLogWatcherService.RulesProvider = () => _settings.GameEventRules;
        _gameLogWatcherService.ErrorOccurred += msg => Log.Warn(msg);
        _gameLogWatcherService.EventMatched += (rule, character, line) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => OnGameEventMatched(rule, character, line));
        };
        _gameLogWatcherService.Start();
    }

    private void OnChatKeywordMatched(ChatAlertRule rule, string channel)
    {
        Log.Info($"Chat alert: '{rule.Keyword}' matched in {channel} (rule character: {(string.IsNullOrWhiteSpace(rule.CharacterName) ? "any" : rule.CharacterName)}).");
        SystemSounds.Exclamation.Play();

        var subtitle = string.IsNullOrWhiteSpace(rule.CharacterName) ? channel : $"{channel} · {rule.CharacterName}";
        ShowToast($"\"{rule.Keyword}\"", subtitle, "#2BC0E4");
    }

    private void OnGameEventMatched(GameEventRule rule, string character, string line)
    {
        var seat = FindSeatByCharacter(character);

        if (rule.SuppressWhenFocused && seat is not null)
        {
            var window = FindAssignedWindows(seat).FirstOrDefault();
            if (window is not null && window.Handle == _windowService.GetForegroundWindowHandle())
                return; // that client is already on screen — no alert needed
        }

        Log.Info($"Game event '{rule.Name}' for {(character.Length > 0 ? character : "unknown character")}: {line}");

        if (rule.FlashOnTile)
        {
            // "Something is happening to this character right now" (combat by default) — pulse the
            // seat's own tile/master rect on the overlay instead of a toast, since a toast per
            // incoming hit would be constant spam. Abyss Mode keeps the visual glow but silences the
            // sound, since Abyssal Deadspace can put up to three characters under continuous,
            // expected damage simultaneously.
            if (rule.PlaySound && !_settings.AbyssModeEnabled) SystemSounds.Exclamation.Play();
            if (seat is not null)
            {
                FlashSeatAlert(seat);
                TriggerCombatGlow(seat);
            }
        }
        else
        {
            if (rule.PlaySound) SystemSounds.Exclamation.Play();
            ShowToast(rule.Name, character.Length > 0 ? character : "", "#F59E0B");
        }
    }

    // Seat currently running the given character: live window title first (RunningCharacterName),
    // then the seat's configured main-character Label as a fallback for logged-off clients.
    private SlotAssignment? FindSeatByCharacter(string character)
    {
        if (string.IsNullOrWhiteSpace(character)) return null;
        return Assignments.FirstOrDefault(a => character.Equals(a.RunningCharacterName, StringComparison.OrdinalIgnoreCase))
            ?? Assignments.FirstOrDefault(a => character.Equals(a.Label, StringComparison.OrdinalIgnoreCase));
    }

    private void OnCharacterSystemChanged(string character, string system)
    {
        if (_systemByCharacter.GetValueOrDefault(character) == system) return;
        _systemByCharacter[character] = system;
        if (_settings.CornerOverlayShowSystem && CornerOverlaysLive) RefreshAllPills();
    }

    // Current solar system for the character seated at the given seat, or "" when unknown.
    private string SeatSystemName(int seat)
    {
        if (!_settings.CornerOverlayShowSystem) return "";
        var a = Seat(seat);
        if (a is null) return "";
        if (!string.IsNullOrWhiteSpace(a.RunningCharacterName)
            && _systemByCharacter.TryGetValue(a.RunningCharacterName, out var bySession))
            return bySession;
        return _systemByCharacter.GetValueOrDefault(a.Label, "");
    }

    private void FlashSeatAlert(SlotAssignment seat)
    {
        seat.IsAlerting = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            seat.IsAlerting = false;
        };
        timer.Start();
    }

    private void AddChatAlertRule()
    {
        _settings.ChatAlertRules.Add(new ChatAlertRule { Keyword = "" });
        Save();
    }

    private void RemoveChatAlertRule(object? parameter)
    {
        if (parameter is not ChatAlertRule rule) return;
        _settings.ChatAlertRules.Remove(rule);
        Save();
    }

    private void AddGameEventRule()
    {
        _settings.GameEventRules.Add(new GameEventRule { Name = "Custom", Pattern = "" });
        Save();
    }

    private void RemoveGameEventRule(object? parameter)
    {
        if (parameter is not GameEventRule rule) return;
        _settings.GameEventRules.Remove(rule);
        Save();
    }

    private void StopChatAlerts()
    {
        _chatLogWatcherService.Stop();
        _gameLogWatcherService.Stop();
    }
}
