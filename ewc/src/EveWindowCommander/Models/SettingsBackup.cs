namespace EveWindowCommander.Models;

public record SettingsBackup(DateTime Timestamp, string Path)
{
    public string DisplayName => $"{Timestamp:yyyy-MM-dd  HH:mm:ss}";
}
