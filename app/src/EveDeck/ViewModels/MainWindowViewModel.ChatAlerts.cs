using System.Collections.ObjectModel;
using System.Media;
using System.Windows.Threading;
using EveDeck.Models;
using Application = System.Windows.Application;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    public ObservableCollection<ChatAlertRule> ChatAlertRules => _settings.ChatAlertRules;

    private void InitChatAlerts()
    {
        AddChatAlertRuleCommand.RaiseCanExecuteChanged();

        _chatLogWatcherService.RulesProvider = () => _settings.ChatAlertRules;
        _chatLogWatcherService.ErrorOccurred += msg => Log.Warn(msg);
        _chatLogWatcherService.KeywordMatched += (rule, channel) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => OnChatKeywordMatched(rule, channel));
        };
        _chatLogWatcherService.Start();
    }

    private void OnChatKeywordMatched(ChatAlertRule rule, string channel)
    {
        Log.Info($"Chat alert: '{rule.Keyword}' matched in {channel} (rule character: {(string.IsNullOrWhiteSpace(rule.CharacterName) ? "any" : rule.CharacterName)}).");
        SystemSounds.Exclamation.Play();

        var matchingSeats = string.IsNullOrWhiteSpace(rule.CharacterName)
            ? Assignments
            : Assignments.Where(a => a.Label.IndexOf(rule.CharacterName, StringComparison.OrdinalIgnoreCase) >= 0
                || channel.IndexOf(a.Label, StringComparison.OrdinalIgnoreCase) >= 0);

        foreach (var seat in matchingSeats)
            FlashSeatAlert(seat);
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

    private void StopChatAlerts() => _chatLogWatcherService.Stop();
}
