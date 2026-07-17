using System.IO;
using System.Media;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;
using Application = System.Windows.Application;

namespace EveDeck.ViewModels;

// Intel-channel jump-distance alert: a system named in the designated intel chatlog channel that is
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

    public string IntelChannelName
    {
        get => _settings.IntelChannelName;
        set
        {
            if (_settings.IntelChannelName == value) return;
            _settings.IntelChannelName = value;
            OnPropertyChanged();
            Save();
        }
    }

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
        if (string.IsNullOrWhiteSpace(_settings.IntelChannelName)) return;
        if (channel.IndexOf(_settings.IntelChannelName, StringComparison.OrdinalIgnoreCase) < 0) return;

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
            SystemSounds.Exclamation.Play();
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
            if (kind == IntelReportKind.Sighting && detail is not null && _shipTypeDictionary is { Count: > 0 } ships)
            {
                var (ship, pilotName) = IntelSystemTokenizer.ResolvePilotAndShip(detail, ships);
                if (ship is { } resolved)
                {
                    primaryDetail = pilotName ?? resolved.Name;
                    secondaryDetail = pilotName is not null ? resolved.Name : null;
                    shipIcon = _shipIconCacheService?.TryGetCachedIcon(resolved.Id);
                }
            }

            ShowIntelToast($"{system} — {jumpText}", kind, primaryDetail, secondaryDetail, messageText, accent, shipIcon);
        }
    }

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
