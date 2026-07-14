using System.IO;
using System.Text;
using EveDeck.Models;

namespace EveDeck.Services;

// Passive tail-watcher over EVE's own plaintext gamelog files (Documents\EVE\logs\Gamelogs\*.txt),
// the same model as ChatLogWatcherService: reading logs EVE itself writes to disk is plain file
// I/O, not memory/packet access, and this service never sends input into an EVE client -- see
// COMPLIANCE.md. Each gamelog belongs to one character, named in its "Listener:" header line.
public sealed class GameLogWatcherService : IDisposable
{
    private readonly string _gamelogsFolder;
    private readonly Dictionary<string, long> _readOffsetByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _listenerByFile = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _resyncTimer;

    // See ChatLogWatcherService's identical fields for why: FileSystemWatcher can silently drop
    // events with nothing here to notice, so an Error handler plus this periodic resync bound how
    // long a missed combat/event line can stay unprocessed.
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(45);

    // Raised for each new line matched against an enabled rule: (rule, character name, matched line).
    public event Action<GameEventRule, string, string>? EventMatched;
    public event Action<string>? ErrorOccurred;

    public Func<IEnumerable<GameEventRule>>? RulesProvider { get; set; }

    public GameLogWatcherService()
    {
        _gamelogsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Gamelogs");
    }

    public void Start()
    {
        try
        {
            if (!Directory.Exists(_gamelogsFolder)) return;

            // Prime offsets to end-of-file for logs that already exist, so history (an afternoon of
            // old combat lines) never fires alerts on startup -- only lines written from now on do.
            var cutoff = DateTime.Now.AddHours(-24);
            foreach (var path in Directory.EnumerateFiles(_gamelogsFolder, "*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff) continue;
                    _readOffsetByFile[Path.GetFileName(path)] = new FileInfo(path).Length;
                    CacheListener(path);
                }
                catch { } // unreadable file — skip; a later Changed event retries
            }

            _watcher = new FileSystemWatcher(_gamelogsFolder, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                InternalBufferSize = 65536,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Error += OnWatcherError;

            _resyncTimer = new System.Threading.Timer(_ => Resync(), null, ResyncInterval, ResyncInterval);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Could not start gamelog watcher: {ex.Message}");
        }
    }

    public void Stop()
    {
        _resyncTimer?.Dispose();
        _resyncTimer = null;
        if (_watcher is null) return;
        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Error -= OnWatcherError;
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

    private void OnWatcherError(object? sender, ErrorEventArgs e)
    {
        ErrorOccurred?.Invoke($"Gamelog watcher lost events, resyncing: {e.GetException().Message}");
        Resync();
    }

    // Catches up on any file a missed/dropped FileSystemWatcher event left un-read — safe to call
    // anytime, since ReadNewLines is purely offset-driven and a no-op past end-of-file.
    private void Resync()
    {
        try
        {
            var cutoff = DateTime.Now.AddHours(-24);
            foreach (var path in Directory.EnumerateFiles(_gamelogsFolder, "*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff) continue;
                    ReadNewLines(path);
                }
                catch { } // unreadable file — skip; the next resync or Changed event retries
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Gamelog resync failed: {ex.Message}");
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

        if (!_listenerByFile.ContainsKey(fileName))
        {
            var listener = ParseListener(text.Split('\n'));
            if (listener is not null) _listenerByFile[fileName] = listener;
        }

        var rules = RulesProvider?.Invoke().Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern)).ToList();
        if (rules is null || rules.Count == 0) return;

        var character = _listenerByFile.GetValueOrDefault(fileName, "");
        foreach (var line in text.Split('\n'))
        {
            // Only timestamped entries ("[ 2026.07.10 04:01:23 ] (combat) ...") are events; header /
            // session-banner lines matching a pattern were the historical false-positive source.
            if (!line.TrimStart().StartsWith('[')) continue;

            foreach (var rule in rules)
            {
                if (line.IndexOf(rule.Pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    EventMatched?.Invoke(rule, character, line.Trim());
            }
        }
    }

    private void CacheListener(string path)
    {
        var fileName = Path.GetFileName(path);
        if (_listenerByFile.ContainsKey(fileName)) return;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.Unicode);
        var headerLines = new List<string>();
        for (var i = 0; i < 8 && reader.ReadLine() is { } line; i++) headerLines.Add(line);
        var listener = ParseListener(headerLines);
        if (listener is not null) _listenerByFile[fileName] = listener;
    }

    // Header block: "  Listener: Character Name" (gamelogs) / "  Listener:        Name" (chatlogs).
    internal static string? ParseListener(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var idx = line.IndexOf("Listener:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var value = line[(idx + "Listener:".Length)..].Trim();
            if (value.Length > 0) return value;
        }
        return null;
    }

    public void Dispose() => Stop();
}
