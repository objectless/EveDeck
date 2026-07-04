using System.IO;
using System.Text;
using EveDeck.Models;

namespace EveDeck.Services;

// Passive tail-watcher over EVE's own plaintext chatlog files (Documents\EVE\logs\Chatlogs\*.txt).
// Reading logs EVE itself writes to disk is plain file I/O, not memory/packet access, and this
// service never sends input into an EVE client — see COMPLIANCE.md.
public sealed class ChatLogWatcherService : IDisposable
{
    private readonly string _chatlogsFolder;
    private readonly Dictionary<string, long> _readOffsetByFile = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;

    // Raised for each new line matched against an enabled rule: (rule, best-effort character/channel name).
    public event Action<ChatAlertRule, string>? KeywordMatched;
    public event Action<string>? ErrorOccurred;

    public Func<IEnumerable<ChatAlertRule>>? RulesProvider { get; set; }

    public ChatLogWatcherService()
    {
        _chatlogsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Chatlogs");
    }

    public void Start()
    {
        try
        {
            if (!Directory.Exists(_chatlogsFolder)) return;

            _watcher = new FileSystemWatcher(_chatlogsFolder, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Could not start chatlog watcher: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_watcher is null) return;
        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        try
        {
            ReadNewLines(e.FullPath);
        }
        catch
        {
            // Best-effort — EVE may hold a competing lock momentarily; the next Changed event retries.
        }
    }

    private void ReadNewLines(string path)
    {
        var fileName = Path.GetFileName(path);
        _readOffsetByFile.TryGetValue(fileName, out var offset);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < offset)
            offset = 0; // file was recreated/truncated (new session) — start from the top

        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.Unicode);
        var text = reader.ReadToEnd();
        _readOffsetByFile[fileName] = stream.Position;

        if (string.IsNullOrEmpty(text)) return;

        var rules = RulesProvider?.Invoke().Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Keyword)).ToList();
        if (rules is null || rules.Count == 0) return;

        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            foreach (var rule in rules)
            {
                if (!string.IsNullOrWhiteSpace(rule.CharacterName)
                    && fileName.IndexOf(rule.CharacterName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (line.IndexOf(rule.Keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    KeywordMatched?.Invoke(rule, ChannelNameFromFile(fileName));
            }
        }
    }

    // Best-effort channel/character prefix from "<channelname>_<id>_<yyyyMMdd>_<HHmmss>.txt".
    private static string ChannelNameFromFile(string fileName)
    {
        var underscore = fileName.IndexOf('_');
        return underscore > 0 ? fileName[..underscore] : fileName;
    }

    public void Dispose() => Stop();
}
