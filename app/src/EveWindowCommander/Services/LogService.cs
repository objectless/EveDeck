using System.Collections.ObjectModel;
using System.IO;
using EveWindowCommander.Models;

namespace EveWindowCommander.Services;

public sealed class LogService
{
    private readonly string _logFolder;
    private readonly string _logPath;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogService(string logFolder)
    {
        _logFolder = logFolder;
        Directory.CreateDirectory(_logFolder);
        _logPath = Path.Combine(_logFolder, $"ewc-{DateTime.Now:yyyyMMdd}.log");
    }

    public void Info(string message) => Write("Info", message);
    public void Warn(string message) => Write("Warn", message);
    public void Error(string message) => Write("Error", message);

    private void Write(string level, string message)
    {
        var entry = new LogEntry { Level = level, Message = message };
        Entries.Insert(0, entry);
        while (Entries.Count > 500)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        File.AppendAllText(_logPath, entry.Display + Environment.NewLine);
    }
}
