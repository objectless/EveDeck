using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;
using Application = System.Windows.Application;

namespace EveDeck.ViewModels;

// Intel-channel jump-distance alert: a system named in any enabled intel chatlog channel that is
// within N jumps of any currently tracked character raises a toast. Built from ChatLogWatcherService's
// per-line LineReceived event (every line, unlike KeywordMatched's per-rule substring hits -- a system
// mention isn't a configured keyword) + SystemJumpGraphService's cached ESI stargate graph.
public sealed partial class MainWindowViewModel
{
    private SystemJumpGraphService? _systemJumpGraphService;
    private SystemJumpGraph? _systemJumpGraph;
    private ShipTypeDictionaryService? _shipTypeDictionaryService;
    private ShipTypeDictionary? _shipTypeDictionary;
    private ShipIconCacheService? _shipIconCacheService;
    private bool _isBuildingIntelMap;

    // A hot intel channel can have several callers repeat the same system in a minute -- this stops
    // that from becoming a toast per line. Keyed by system name so different systems alert independently.
    private readonly Dictionary<string, DateTime> _intelAlertedAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan IntelAlertCooldown = TimeSpan.FromMinutes(5);

    public bool IntelJumpAlertEnabled
    {
        get => _settings.IntelJumpAlertEnabled;
        set
        {
            if (_settings.IntelJumpAlertEnabled == value) return;
            _settings.IntelJumpAlertEnabled = value;
            OnPropertyChanged();
            Save();
            // Lazy build on first enable, not on every app start -- the crawl takes real wall-clock
            // time and topology essentially never changes once cached.
            if (value && (_systemJumpGraph is null || _shipTypeDictionary is null)) _ = BuildSystemJumpGraphAsync();
        }
    }

    // Every locally-discovered chatlog channel the user has reviewed, each independently
    // enabled/disabled with its own alert sound -- see ChatLogWatcherService.DiscoverChannels() and
    // DiscoverIntelChannels/RefreshIntelChannelsCommand below.
    public ObservableCollection<IntelChannelRule> IntelChannelRules => _settings.IntelChannelRules;

    public RelayCommand RefreshIntelChannelsCommand { get; private set; } = null!;
    public RelayCommand RemoveIntelChannelRuleCommand { get; private set; } = null!;

    public IReadOnlyList<IntelAlertSound> IntelAlertSoundOptions { get; } = Enum.GetValues<IntelAlertSound>();

    public int IntelJumpAlertMaxJumps
    {
        get => _settings.IntelJumpAlertMaxJumps;
        set
        {
            var clamped = Math.Clamp(value, 1, 50);
            if (_settings.IntelJumpAlertMaxJumps == clamped) return;
            _settings.IntelJumpAlertMaxJumps = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    private string _intelMapStatus = "System map not built yet.";
    public string IntelMapStatus
    {
        get => _intelMapStatus;
        private set => SetProperty(ref _intelMapStatus, value);
    }

    public RelayCommand BuildIntelMapCommand { get; private set; } = null!;

    private void InitIntelJumpAlert()
    {
        BuildIntelMapCommand = new RelayCommand(() => _ = BuildSystemJumpGraphAsync(), () => !_isBuildingIntelMap);
        RefreshIntelChannelsCommand = new RelayCommand(DiscoverIntelChannels);
        RemoveIntelChannelRuleCommand = new RelayCommand(parameter =>
        {
            if (parameter is not IntelChannelRule rule) return;
            _settings.IntelChannelRules.Remove(rule);
            Save();
        }, parameter => parameter is IntelChannelRule);

        // One-time migration: the old single-channel setting becomes the first (enabled) entry in
        // the new per-channel list, so upgrading users don't lose their configured intel channel.
        // IntelChannelRules stays non-empty forever after a real channel gets added, so this can
        // only ever fire once per install (a user who deliberately empties the list back to zero
        // entries just gets nothing re-migrated, since IntelChannelName isn't cleared -- an edge
        // case not worth guarding, re-adding a channel by hand there is a one-click Refresh away).
        if (_settings.IntelChannelRules.Count == 0 && !string.IsNullOrWhiteSpace(_settings.IntelChannelName))
        {
            _settings.IntelChannelRules.Add(new IntelChannelRule { ChannelName = _settings.IntelChannelName, Enabled = true });
            Save();
        }
        DiscoverIntelChannels();

        _chatLogWatcherService.LineReceived += (channel, line) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => OnIntelLineReceived(channel, line));
        };

        _systemJumpGraphService = new SystemJumpGraphService(_configService.AppDataFolder);
        _shipTypeDictionaryService = new ShipTypeDictionaryService(_configService.AppDataFolder);
        _shipIconCacheService = new ShipIconCacheService(_configService.AppDataFolder);

        // User's own cache wins if present (it's either a prior live crawl or an already-persisted
        // seed -- see TryPersistSeed below); otherwise fall back to the snapshot bundled with the
        // app so a fresh install doesn't have to wait through its own ~2-minute ESI crawl before
        // Intel Alerts works at all. "Build system map" still does a live re-crawl for anyone who
        // wants current data sooner (e.g. right after a new expansion).
        var cachedGraph = _systemJumpGraphService.TryLoadCached() ?? LoadBundledSeedGraph();
        var cachedShips = _shipTypeDictionaryService.TryLoadCached() ?? LoadBundledSeedShips();
        if (cachedGraph is not null) _systemJumpGraph = cachedGraph;
        if (cachedShips is not null) _shipTypeDictionary = cachedShips;

        if (cachedGraph is not null && cachedShips is not null)
        {
            IntelMapStatus = $"System map ready ({cachedGraph.Count} systems, {cachedShips.Count} ship types).";
            _ = _shipIconCacheService.EnsureIconsCachedAsync(cachedShips.AllIds, CancellationToken.None);
        }
        else if (_settings.IntelJumpAlertEnabled)
        {
            _ = BuildSystemJumpGraphAsync();
        }
    }

    // Adds any newly-discovered chatlog channel as a new (disabled) IntelChannelRule -- never
    // touches or removes existing entries, so per-channel Enabled/Sound choices survive a refresh.
    // New discoveries start disabled: alerting shouldn't silently turn on for a channel the user
    // hasn't deliberately reviewed and opted into (same "don't guess" convention as the Jabber
    // ping's required-phrase filter).
    private void DiscoverIntelChannels()
    {
        var found = _chatLogWatcherService.DiscoverChannels();
        var existing = new HashSet<string>(
            _settings.IntelChannelRules.Select(r => r.ChannelName), StringComparer.OrdinalIgnoreCase);

        foreach (var name in found)
        {
            if (existing.Contains(name)) continue;
            _settings.IntelChannelRules.Add(new IntelChannelRule { ChannelName = name, Enabled = false });
        }

        // Alliance/Corp/Fleet got auto-added by older versions of this method before they were
        // excluded from discovery -- drop those stale entries too, as long as the user never
        // enabled one (an enabled entry means they deliberately opted in despite the exclusion,
        // so leave it alone rather than silently ripping out a live alert).
        var stale = _settings.IntelChannelRules
            .Where(r => !r.Enabled && ChatLogWatcherService.IsExcludedChannelName(r.ChannelName))
            .ToList();
        foreach (var rule in stale) _settings.IntelChannelRules.Remove(rule);

        Save();
    }

    // Falls back to the system-jump graph snapshot bundled next to the exe (Assets\SeedData) when
    // the user has no cache of their own yet. Persists it into the real cache path on success so
    // every later launch (and the manual "Build system map" refresh) reads/writes one consistent
    // location instead of re-reading the bundled file each time.
    private SystemJumpGraph? LoadBundledSeedGraph()
    {
        var seedPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SeedData", "system-jump-graph.json");
        var graph = SystemJumpGraphService.TryLoadFrom(seedPath);
        if (graph is not null) TryPersistSeed(seedPath, _configService.AppDataFolder, "system-jump-graph.json");
        return graph;
    }

    private ShipTypeDictionary? LoadBundledSeedShips()
    {
        var seedPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SeedData", "ship-type-names.json");
        var ships = ShipTypeDictionaryService.TryLoadFrom(seedPath);
        if (ships is not null) TryPersistSeed(seedPath, _configService.AppDataFolder, "ship-type-names.json");
        return ships;
    }

    private static void TryPersistSeed(string seedPath, string appDataFolder, string cacheFileName)
    {
        try
        {
            var cacheDir = Path.Combine(appDataFolder, "cache");
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, cacheFileName);
            if (!File.Exists(cachePath)) File.Copy(seedPath, cachePath);
        }
        catch
        {
            // Best-effort -- worst case this re-reads the bundled seed on the next launch too.
        }
    }

    // Builds the system-jump graph and the ship-type dictionary together (independent ESI crawls,
    // run concurrently -- no reason to serialize them). The ship dictionary is a much smaller crawl
    // and normally finishes well before the system map's own progress reporting does, so the visible
    // status text is still dominated by the system map's live "done/total" updates.
    private async Task BuildSystemJumpGraphAsync()
    {
        if (_isBuildingIntelMap || _systemJumpGraphService is null || _shipTypeDictionaryService is null) return;
        _isBuildingIntelMap = true;
        BuildIntelMapCommand.RaiseCanExecuteChanged();
        IntelMapStatus = "Building system map + ship dictionary from ESI (first time only, may take a few minutes)...";
        try
        {
            var progress = new Progress<(int done, int total)>(p =>
                IntelMapStatus = $"Building system map from ESI... {p.done}/{p.total}");
            var systemTask = _systemJumpGraphService.BuildAsync(progress, CancellationToken.None);
            var shipTask = _shipTypeDictionaryService.BuildAsync(CancellationToken.None);
            await Task.WhenAll(systemTask, shipTask);

            _systemJumpGraph = systemTask.Result;
            _shipTypeDictionary = shipTask.Result;
            IntelMapStatus = systemTask.Result.Count > 0
                ? $"System map ready ({systemTask.Result.Count} systems, {shipTask.Result.Count} ship types)."
                : "System map build returned no data -- check your internet connection and try again.";

            if (_shipIconCacheService is not null)
                _ = _shipIconCacheService.EnsureIconsCachedAsync(shipTask.Result.AllIds, CancellationToken.None);
        }
        catch (Exception ex)
        {
            IntelMapStatus = $"System map build failed: {ex.Message}";
            Log.Warn($"Intel system map build failed: {ex.Message}");
        }
        finally
        {
            _isBuildingIntelMap = false;
            BuildIntelMapCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnIntelLineReceived(string channel, string line)
    {
        if (!_settings.IntelJumpAlertEnabled) return;
        if (_systemJumpGraph is not { Count: > 0 } graph) return;

        // First enabled rule whose channel name is a substring match against this line's actual
        // channel wins -- same substring convention used everywhere else in this app (ChatAlertRule,
        // JabberPingConversationName). Multiple enabled rules matching the same channel is an
        // unlikely, harmless edge case (one channel just alerts with whichever rule's sound sorts
        // first); nothing currently needs per-rule-precedence to be more precise than that.
        var matchedRule = _settings.IntelChannelRules.FirstOrDefault(r =>
            r.Enabled && channel.IndexOf(r.ChannelName, StringComparison.OrdinalIgnoreCase) >= 0);
        if (matchedRule is null) return;

        var messageText = ExtractMessageText(line);
        var mentions = IntelSystemTokenizer.FindSystemMentionsWithTrailingText(messageText, graph);
        if (mentions.Count == 0) return;

        var mySystems = CurrentTrackedSystems();
        if (mySystems.Count == 0) return;

        var maxJumps = _settings.IntelJumpAlertMaxJumps;
        var now = DateTime.UtcNow;

        foreach (var mention in mentions)
        {
            var system = mention.SystemName;
            if (_intelAlertedAt.TryGetValue(system, out var lastAlert) && now - lastAlert < IntelAlertCooldown)
                continue;

            int? closest = null;
            foreach (var mine in mySystems)
            {
                var distance = graph.DistanceBetween(mine, system, maxJumps);
                if (distance is int d && (closest is null || d < closest)) closest = d;
            }
            if (closest is not int jumps) continue;

            _intelAlertedAt[system] = now;
            PlayIntelSound(matchedRule.Sound);
            var jumpText = jumps switch { 0 => "in your system", 1 => "1 jump away", _ => $"{jumps} jumps away" };
            var (kind, detail) = IntelSystemTokenizer.ClassifyTrailingText(mention.TrailingText);
            var accent = kind == IntelReportKind.Clear ? "#22C55E" : "#8B5CF6";

            // For a Sighting, try to split "Ultrabug Tholos" into a pilot name and the ship they're
            // in (also recognizes community abbreviations like "CFI" -> Cyclone Fleet Issue) rather
            // than showing the two words as one undifferentiated blob -- only when the ship
            // dictionary is actually loaded; otherwise this degrades to the old flat-detail behavior.
            var primaryDetail = detail;
            string? secondaryDetail = null;
            System.Windows.Media.ImageSource? shipIcon = null;
            string? zkillUrl = null;
            if (kind == IntelReportKind.Sighting && detail is not null && _shipTypeDictionary is { Count: > 0 } ships)
            {
                var (ship, pilotName) = IntelSystemTokenizer.ResolvePilotAndShip(detail, ships);
                if (ship is { } resolved)
                {
                    primaryDetail = pilotName ?? resolved.Name;
                    secondaryDetail = pilotName is not null ? resolved.Name : null;
                    shipIcon = _shipIconCacheService?.TryGetCachedIcon(resolved.Id);
                    if (pilotName is not null) zkillUrl = BuildZkillSearchUrl(pilotName);
                }
            }

            Action? openZkill = zkillUrl is null ? null : () =>
            {
                try { Process.Start(new ProcessStartInfo(zkillUrl) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Warn($"Could not open zKillboard link: {ex.Message}"); }
            };
            ShowIntelToast($"{system} — {jumpText}", kind, primaryDetail, secondaryDetail, messageText, accent, shipIcon, openZkill, zkillUrl);
        }
    }

    private static void PlayIntelSound(IntelAlertSound sound)
    {
        var systemSound = sound switch
        {
            IntelAlertSound.Asterisk => SystemSounds.Asterisk,
            IntelAlertSound.Hand => SystemSounds.Hand,
            IntelAlertSound.Question => SystemSounds.Question,
            IntelAlertSound.Beep => SystemSounds.Beep,
            _ => SystemSounds.Exclamation,
        };
        systemSound.Play();
    }

    // zKillboard's own /search/<name>/ page resolves a character name directly (confirmed working
    // live) -- no local ESI name->ID resolution or zKillboard API call needed at all, this just
    // opens a browser tab to their search results the same way the Jabber ping toast's "click to
    // join comms" link works. EVEWho was considered too (the user's other named option) but its
    // Cloudflare protection blocks even basic verification of its search URL shape, so it was
    // dropped rather than risk shipping a link that might be wrong.
    private static string BuildZkillSearchUrl(string pilotName)
        => $"https://zkillboard.com/search/{Uri.EscapeDataString(pilotName)}/";

    // "[ 2026.07.10 04:01:23 ] CallerName > text..." -- strip the timestamp+speaker prefix so the
    // tokenizer isn't scanning the timestamp's own digits as candidate words, and the toast body
    // doesn't repeat it either.
    private static string ExtractMessageText(string line)
    {
        var idx = line.IndexOf('>');
        return (idx >= 0 && idx + 1 < line.Length ? line[(idx + 1)..] : line).Trim();
    }

    // Deliberately reads _systemByCharacter directly rather than reusing SeatSystemName -- that
    // helper is gated on CornerOverlayShowSystem (a display toggle for the corner-overlay pills),
    // which has nothing to do with whether a character's system is actually known. Gating this
    // feature on an unrelated display setting would silently break it for anyone with that toggle
    // off. Mirrors FindSeatByCharacter's own character-resolution fallback (RunningCharacterName,
    // then Label) instead.
    private List<string> CurrentTrackedSystems()
    {
        var systems = new List<string>();
        foreach (var seat in Assignments)
        {
            var name = !string.IsNullOrWhiteSpace(seat.RunningCharacterName) ? seat.RunningCharacterName : seat.Label;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!_systemByCharacter.TryGetValue(name, out var system) || string.IsNullOrWhiteSpace(system)) continue;
            if (!systems.Contains(system, StringComparer.OrdinalIgnoreCase)) systems.Add(system);
        }
        return systems;
    }
}
