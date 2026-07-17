using System.IO;
using System.Text;
using EveDeck.Models;

namespace EveDeck.Services;

// Passive tail-watcher over EVE's own plaintext chatlog files (Documents\EVE\logs\Chatlogs\*.txt).
// Reading logs EVE itself writes to disk is plain file I/O, not memory/packet access, and this
// service never sends input into an EVE client — see COMPLIANCE.md.
public sealed class ChatLogWatcherService : IDisposable
{
    private const string LocalSystemMarker = "Channel changed to Local : ";

    private readonly string _chatlogsFolder;
    private readonly Dictionary<string, long> _readOffsetByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _listenerByFile = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _resyncTimer;

    // FileSystemWatcher can silently drop events (internal buffer overflow under a burst of writes
    // across many characters' log files at once -- fleet chat, combat, several simultaneous jumps --
    // or plain OS-level flakiness) with no exception anywhere in this class to catch; the only signal
    // is the watcher's own Error event, which nothing was listening for. A dropped "Channel changed
    // to Local" line then leaves a character's last-known system permanently stale until some other,
    // unrelated line happens to be appended to that same file later. Both the Error handler and this
    // periodic resync exist purely to bound how long that staleness can last -- confirmed via a real
    // report where the chatlog file on disk had the correct system line but the overlay never picked
    // it up (see project-location-tracking-resync memory).
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(45);

    // Raised for each new line matched against an enabled rule: (rule, best-effort character/channel name).
    public event Action<ChatAlertRule, string>? KeywordMatched;
    // Raised when a character's Local channel reports a solar-system change: (character, system name).
    public event Action<string, string>? SystemChanged;
    // Raised for EVERY new timestamped chat line, regardless of whether any rule matched it: (channel,
    // raw line text). KeywordMatched only fires for substring hits against a configured rule, which
    // can't answer "does this line mention any of ~8000 system names" -- that needs to see every line
    // and run its own lookup (IntelSystemTokenizer), not a per-rule substring check.
    public event Action<string, string>? LineReceived;
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

            // Prime offsets to end-of-file for logs that already exist so keyword rules never fire
            // on history when the app starts mid-session. Local files still get one full read here
            // (alert-free) so the CURRENT solar system is known immediately, not only after the
            // next jump.
            var cutoff = DateTime.Now.AddHours(-24);
            foreach (var path in Directory.EnumerateFiles(_chatlogsFolder, "*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff) continue;
                    var fileName = Path.GetFileName(path);
                    if (fileName.StartsWith("Local_", StringComparison.OrdinalIgnoreCase))
                    {
                        ReadNewLines(path, matchKeywords: false);
                    }
                    else
                    {
                        _readOffsetByFile[fileName] = new FileInfo(path).Length;
                    }
                }
                catch { } // unreadable file — skip; a later Changed event retries
            }

            _watcher = new FileSystemWatcher(_chatlogsFolder, "*.txt")
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
            ErrorOccurred?.Invoke($"Could not start chatlog watcher: {ex.Message}");
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
            ReadNewLines(e.FullPath, matchKeywords: true);
        }
        catch
        {
            // Best-effort — EVE may hold a competing lock momentarily; the next Changed event retries.
        }
    }

    private void OnWatcherError(object? sender, ErrorEventArgs e)
    {
        ErrorOccurred?.Invoke($"Chatlog watcher lost events, resyncing: {e.GetException().Message}");
        Resync();
    }

    // Catches up on any file this service has already seen (or should have) that a missed/dropped
    // FileSystemWatcher event left un-read -- safe to call anytime, including a Local_ file with no
    // new content, since ReadNewLines is purely offset-driven and a no-op past end-of-file.
    private void Resync()
    {
        try
        {
            var cutoff = DateTime.Now.AddHours(-24);
            foreach (var path in Directory.EnumerateFiles(_chatlogsFolder, "*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff) continue;
                    ReadNewLines(path, matchKeywords: true);
                }
                catch { } // unreadable file — skip; the next resync or Changed event retries
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Chatlog resync failed: {ex.Message}");
        }
    }

    private void ReadNewLines(string path, bool matchKeywords)
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

        var lines = text.Split('\n');

        if (!_listenerByFile.ContainsKey(fileName))
        {
            var listener = GameLogWatcherService.ParseListener(lines);
            if (listener is not null) _listenerByFile[fileName] = listener;
        }

        var isLocal = fileName.StartsWith("Local_", StringComparison.OrdinalIgnoreCase);
        if (isLocal)
        {
            // Last system-change line wins (a primed full read may contain several jumps).
            string? system = null;
            foreach (var line in lines)
            {
                var idx = line.IndexOf(LocalSystemMarker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) system = line[(idx + LocalSystemMarker.Length)..].Trim();
            }
            var character = _listenerByFile.GetValueOrDefault(fileName, "");
            if (!string.IsNullOrEmpty(system) && character.Length > 0)
                SystemChanged?.Invoke(character, system);
        }

        if (!matchKeywords) return;

        var rules = RulesProvider?.Invoke().Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Keyword)).ToList();
        var channel = ChannelNameFromFile(fileName);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Only timestamped chat entries ("[ 2026.07.10 04:01:23 ] Name > text") are messages;
            // session-header/MOTD banner text matching a keyword was a false-positive source on relog.
            if (!line.TrimStart().StartsWith('[')) continue;

            LineReceived?.Invoke(channel, line);

            if (rules is null) continue;
            foreach (var rule in rules)
            {
                if (!string.IsNullOrWhiteSpace(rule.CharacterName)
                    && fileName.IndexOf(rule.CharacterName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (line.IndexOf(rule.Keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    KeywordMatched?.Invoke(rule, channel);
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
