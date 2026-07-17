using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace EveDeck.Services;

// Passive tail-watcher over Pidgin's own HTML conversation logs
// (%APPDATA%\.purple\logs\jabber\<account>\<conversation>\*.html). This is the log-file-reading
// sibling of ChatLogWatcherService/GameLogWatcherService, not a new login/identity: Pidgin already
// owns the real Jabber session, EveDeck only reads what Pidgin itself already wrote to disk (the
// user must enable logging for the conversation in Pidgin's own Preferences > Logging). No XMPP
// credentials are read, stored, or connected with here.
//
// Unlike EVE's own logs (one file per session, appended to for the session's whole lifetime),
// Pidgin starts a FRESH file per conversation "session" (reopening the window, an idle gap, a
// Pidgin restart) -- several files can exist per conversation folder across a single day. This
// needs no special handling beyond what ChatLogWatcherService already does: a newly Created file
// has no entry in _readOffsetByFile yet, so it's read from byte 0 and every line in it is treated
// as new, which is exactly correct for a session that just started.
public sealed class JabberPingWatcherService : IDisposable
{
    private readonly string _logsRoot;
    private readonly Dictionary<string, long> _readOffsetByFile = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _resyncTimer;

    // Same rationale as ChatLogWatcherService: FileSystemWatcher can silently drop events, so a
    // periodic resync bounds how long a missed ping can stay unnoticed.
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(45);

    // Raised for each real chat message (not join/leave/topic system lines) in a conversation
    // folder whose name matches the configured filter: (senderName, cleanedMessageText).
    public event Action<string, string>? MessageReceived;
    public event Action<string>? ErrorOccurred;

    // Substring match (case-insensitive) against the containing conversation folder's name, e.g.
    // "directorbot" matches ...\jabber\<account>\directorbot@goonfleet.com\*.html, "beehive" matches
    // ...\beehive@conference.goonfleet.com.chat\*.html. Null/empty matches nothing (feature is off).
    public Func<string?>? ConversationNameFilterProvider { get; set; }

    // Pidgin's HTML log writer marks a real spoken/broadcast message with a colored sender span;
    // system lines (join/leave/topic-change) use a plain span with no color attribute at all, so
    // this pattern naturally excludes them rather than needing a separate negative check. Verified
    // directly against real Pidgin 2.x log output, both a 1:1 IM and a MUC room.
    private static readonly Regex MessageLineRegex = new(
        @"<span style=""color:[^""]*""><span style=""font-size:\s*smaller"">\([^)]*\)</span>\s*<b>(?<sender>.*?):</b></span>\s*(?<body>.*?)<br\s*/?>\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BreakTagRegex = new(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnyTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    // Some ping/broadcast tools pad label:value lines with a long run of zero-width Unicode
    // characters (ZWSP/ZWNJ/ZWJ/word-joiner/BOM) to visually fake a tab stop in a proportional
    // font -- collapsed to a single space (not deleted outright) so "Formup:<padding>C-J6MT" reads
    // as "Formup: C-J6MT" instead of gluing together with no space at all.
    private static readonly Regex ZeroWidthRunRegex = new("[​‌‍⁠﻿]+", RegexOptions.Compiled);
    private static readonly Regex RepeatedHorizontalWhitespaceRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);

    // Real GoonPinger broadcasts include a "Comms:" field alongside "Formup:"/"FC:" (see
    // JabberPingWatcherServiceTests for the real captured Formup/FC shape this mirrors) naming the
    // voice channel to join. Multiline so ^/$ match per physical line of the already-cleaned body
    // (CleanMessageBody turns each <br/> into its own \n).
    private static readonly Regex CommsFieldRegex = new(
        @"^\s*Comms:\s*(?<value>.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // True once Start() has successfully stood up the FileSystemWatcher. Surfaced in the UI so "is
    // this actually running" isn't a silent guess -- see AppSettings.JabberPingEnabled's Options tab.
    public bool IsRunning { get; private set; }

    public JabberPingWatcherService()
    {
        _logsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".purple", "logs", "jabber");
    }

    public void Start()
    {
        try
        {
            if (!Directory.Exists(_logsRoot))
            {
                // Previously a silent no-op -- the bridge would just sit there forever doing nothing
                // with zero feedback anywhere. This is the normal state for a Pidgin that has never
                // had logging enabled/wasn't used yet, so it's worth surfacing rather than guessing.
                ErrorOccurred?.Invoke(
                    $"Jabber logs folder not found at {_logsRoot} -- enable logging in Pidgin " +
                    "(Tools > Preferences > Logging) for the conversation you want to bridge, then " +
                    "send or receive at least one message so Pidgin creates the folder.");
                return;
            }

            // Prime offsets to end-of-file for logs that already exist so the app never alerts on
            // history from before it started -- same convention as ChatLogWatcherService/
            // GameLogWatcherService.
            var cutoff = DateTime.Now.AddHours(-24);
            foreach (var path in Directory.EnumerateFiles(_logsRoot, "*.html", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff) continue;
                    _readOffsetByFile[path] = new FileInfo(path).Length;
                }
                catch { } // unreadable file — skip; a later Changed event retries
            }

            _watcher = new FileSystemWatcher(_logsRoot, "*.html")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                InternalBufferSize = 65536,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Error += OnWatcherError;

            _resyncTimer = new System.Threading.Timer(_ => Resync(), null, ResyncInterval, ResyncInterval);
            IsRunning = true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Could not start Jabber ping watcher: {ex.Message}");
        }
    }

    public void Stop()
    {
        IsRunning = false;
        _resyncTimer?.Dispose();
        _resyncTimer = null;
        if (_watcher is null) return;
        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
    }

    // Pulls a "Comms: <channel>" field out of an already-cleaned ping body, e.g. "Comms: Fleet 1" or
    // "Comms: TypeX/Fleet". Null when the ping didn't include one -- most pings won't, this is
    // opportunistic. Pure, no I/O, mirrors TryParseMessageLine's own testable shape.
    internal static string? TryExtractCommsChannel(string body)
    {
        var match = CommsFieldRegex.Match(body);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        try
        {
            ReadNewLines(e.FullPath);
        }
        catch
        {
            // Best-effort — Pidgin may hold a competing lock momentarily; the next event retries.
        }
    }

    private void OnWatcherError(object? sender, ErrorEventArgs e)
    {
        ErrorOccurred?.Invoke($"Jabber ping watcher lost events, resyncing: {e.GetException().Message}");
        Resync();
    }

    private void Resync()
    {
        try
        {
            var cutoff = DateTime.Now.AddHours(-24);
            foreach (var path in Directory.EnumerateFiles(_logsRoot, "*.html", SearchOption.AllDirectories))
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
            ErrorOccurred?.Invoke($"Jabber ping resync failed: {ex.Message}");
        }
    }

    private void ReadNewLines(string path)
    {
        var filter = ConversationNameFilterProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(filter)) return;

        var conversationFolder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        if (conversationFolder.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) return;

        _readOffsetByFile.TryGetValue(path, out var offset);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < offset)
            offset = 0; // file was recreated/truncated — start from the top

        stream.Seek(offset, SeekOrigin.Begin);
        // Pidgin's own <meta charset="utf-8"> in every log file -- deliberately NOT the UTF-16
        // Encoding.Unicode used for EVE's own chat/game logs, this is a different tool's log format.
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var text = reader.ReadToEnd();
        _readOffsetByFile[path] = stream.Position;

        if (string.IsNullOrEmpty(text)) return;

        foreach (var line in text.Split('\n'))
        {
            var parsed = TryParseMessageLine(line);
            if (parsed is not var (messageSender, body)) continue;
            MessageReceived?.Invoke(messageSender, body);
        }
    }

    // Pure line parser, no I/O -- pulled out of ReadNewLines so it's directly unit-testable against
    // real captured Pidgin HTML fragments, mirroring GameLogWatcherService.ParseListener's shape.
    // Null for a system line (join/leave/topic-change), a mid-record fragment of a multi-physical-line
    // system block, or a real message whose body was empty/whitespace after cleaning.
    internal static (string Sender, string Body)? TryParseMessageLine(string rawLine)
    {
        var match = MessageLineRegex.Match(rawLine);
        if (!match.Success) return null;

        var sender = match.Groups["sender"].Value.Trim();
        var body = CleanMessageBody(match.Groups["body"].Value);
        return body.Length == 0 ? null : (sender, body);
    }

    private static string CleanMessageBody(string rawHtml)
    {
        var text = BreakTagRegex.Replace(rawHtml, "\n");
        text = AnyTagRegex.Replace(text, ""); // drop any remaining tags (nested XHTML-IM <a>/<html> wrappers etc), keeping their inner text
        text = WebUtility.HtmlDecode(text);
        text = ZeroWidthRunRegex.Replace(text, " ");
        text = RepeatedHorizontalWhitespaceRegex.Replace(text, " ");
        return text.Trim();
    }

    public void Dispose() => Stop();
}
