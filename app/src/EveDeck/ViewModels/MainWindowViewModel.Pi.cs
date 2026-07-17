using System.Collections.ObjectModel;
using System.Media;
using System.Windows.Threading;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;

namespace EveDeck.ViewModels;

// Planetary Industry tab: a read-only ESI colony monitor (extractor expiry, storage fill) plus a
// factory-load calculator. Reuses the seat-flash + sound alert path already used by Chat/Game alerts.
public sealed partial class MainWindowViewModel
{
    private PlanetaryIndustryService? _piService;
    private EsiTypeCache? _piTypes;

    // Colonies poll rarely (ESI caches ~10 min); the countdown display ticks every 30s in between.
    private readonly DispatcherTimer _piPollTimer = new();
    private readonly DispatcherTimer _piCountdownTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    // Keys (charId:planetId:productId / charId:planetId storage) currently inside their alert window,
    // so an alert sounds once on entry rather than on every countdown tick.
    private readonly HashSet<string> _piAlerted = new();
    private bool _piRefreshInProgress;

    public ObservableCollection<PiColony> PiColonies { get; } = new();
    public ObservableCollection<PiFactoryInput> PiFactoryInputs { get; } = new();
    public ObservableCollection<PiCalcRow> PiCalcRows { get; } = new();

    public RelayCommand RefreshPiCommand { get; private set; } = null!;
    public RelayCommand RunFactoryCalcCommand { get; private set; } = null!;
    public RelayCommand AddFactoryInputCommand { get; private set; } = null!;
    public RelayCommand RemoveFactoryInputCommand { get; private set; } = null!;

    private void InitPi()
    {
        RefreshPiCommand = new RelayCommand(async () => await RefreshPiAsync());
        RunFactoryCalcCommand = new RelayCommand(async () => await RunFactoryCalcAsync());
        AddFactoryInputCommand = new RelayCommand(async () => await AddFactoryInputAsync());
        RemoveFactoryInputCommand = new RelayCommand(RemoveFactoryInput);

        _piPollTimer.Tick += async (_, _) => await RefreshPiAsync();
        _piCountdownTimer.Tick += (_, _) => RefreshCountdownsAndAlerts();

        // Restore configured factory inputs (names resolve lazily on first poll/calc).
        foreach (var id in _settings.PiFactoryInputTypeIds)
            PiFactoryInputs.Add(new PiFactoryInput { TypeId = id, Name = $"Type {id}" });

        if (PiEnabled) StartPi();
    }

    private void EnsurePiServices()
    {
        if (_piService is not null) return;
        _piTypes = new EsiTypeCache(_configService.AppDataFolder);
        var client = new EsiClient(_esiAuth, TokenStore);
        _piService = new PlanetaryIndustryService(client, _piTypes);
    }

    // ── Enable toggle ─────────────────────────────────────────────────────────

    public bool PiEnabled
    {
        get => _settings.PiEnabled;
        set
        {
            if (_settings.PiEnabled == value) return;
            _settings.PiEnabled = value;
            OnPropertyChanged();
            Save();
            if (value) StartPi(); else StopPi();
        }
    }

    private void StartPi()
    {
        EnsurePiServices();
        _piPollTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.PiRefreshMinutes));
        _piPollTimer.Start();
        _piCountdownTimer.Start();
        _ = RefreshPiAsync();
    }

    private void StopPi()
    {
        _piPollTimer.Stop();
        _piCountdownTimer.Stop();
    }

    private string _piStatus = "";
    public string PiStatus { get => _piStatus; set => SetProperty(ref _piStatus, value); }

    public int PiRefreshMinutes
    {
        get => _settings.PiRefreshMinutes;
        set
        {
            var v = Math.Max(1, value);
            if (_settings.PiRefreshMinutes == v) return;
            _settings.PiRefreshMinutes = v;
            OnPropertyChanged();
            _piPollTimer.Interval = TimeSpan.FromMinutes(v);
            Save();
        }
    }

    public double PiExtractorAlertHours
    {
        get => _settings.PiExtractorAlertHours;
        set { if (_settings.PiExtractorAlertHours == value) return; _settings.PiExtractorAlertHours = Math.Max(0, value); OnPropertyChanged(); Save(); }
    }

    public int PiStorageAlertPercent
    {
        get => _settings.PiStorageAlertPercent;
        set { var v = Math.Clamp(value, 1, 100); if (_settings.PiStorageAlertPercent == v) return; _settings.PiStorageAlertPercent = v; OnPropertyChanged(); Save(); }
    }

    // ── Colony poll ───────────────────────────────────────────────────────────

    private async Task RefreshPiAsync()
    {
        if (_piRefreshInProgress) return;
        var linked = Assignments
            .SelectMany(a => a.EsiCharacters.Select(c => (c.CharacterId, a.SlotNumber, c.CharacterName)))
            .Where(t => TokenStore.Has(t.CharacterId))
            .ToList();

        if (linked.Count == 0)
        {
            PiColonies.Clear();
            PiStatus = "No linked characters with Planetary Industry access. Link a character (Clients tab) with the PI scope.";
            return;
        }

        _piRefreshInProgress = true;
        PiStatus = "Loading colonies from ESI…";
        try
        {
            EnsurePiServices();
            var errors = new List<string>();
            var colonies = await _piService!.FetchColoniesAsync(linked, e => errors.Add(e), CancellationToken.None);

            // Colonies are rebuilt wholesale every poll (fresh PiColony instances), so carry forward
            // which ones the user had expanded rather than collapsing everything back on every refresh.
            var expanded = PiColonies.Where(c => c.IsExpanded).Select(PiColonyKey).ToHashSet();

            PiColonies.Clear();
            foreach (var c in colonies.OrderBy(c => c.NextExpiry ?? DateTimeOffset.MaxValue))
            {
                c.IsExpanded = expanded.Contains(PiColonyKey(c));
                PiColonies.Add(c);
            }

            RefreshCountdownsAndAlerts();

            // Auto-populate the factory-load calculator from whatever P1s the account's own colonies
            // are actually producing (discovered above via schematics), then run the split.
            await SyncFactoryInputsAsync(CancellationToken.None);
            await RunFactoryCalcAsync();

            var when = DateTime.Now.ToString("HH:mm");
            PiStatus = errors.Count == 0
                ? $"{colonies.Count} colonies across {linked.Count} characters — updated {when}."
                : $"{colonies.Count} colonies (updated {when}). {errors.Count} character(s) had errors: {errors[0]}";
            foreach (var e in errors) Log.Warn(e);
        }
        catch (Exception ex)
        {
            PiStatus = $"PI refresh failed: {ex.Message}";
            Log.Error($"PI refresh failed: {ex.Message}");
        }
        finally
        {
            _piRefreshInProgress = false;
        }
    }

    // Recompute every extractor's countdown/state against 'now' and collect newly-triggered alerts
    // (extractor-expiry or storage-full windows), then raise them as ONE bundled toast per refresh
    // pass instead of one per colony -- a multi-colony empire could otherwise fire a dozen at once.
    private void RefreshCountdownsAndAlerts()
    {
        var now = DateTimeOffset.UtcNow;
        var alertHours = _settings.PiExtractorAlertHours;
        var storagePct = _settings.PiStorageAlertPercent;

        // Newly-triggered alerts grouped by owning character, first-seen order preserved (a character
        // can own several colonies, not necessarily contiguous in PiColonies). Rendered as one toast
        // group per character so the name is a header, not repeated on every planet row.
        var byCharacter = new List<(string Character, List<Views.ToastLine> Lines)>();
        var alertCount = 0;

        List<Views.ToastLine> LinesFor(string character)
        {
            foreach (var g in byCharacter)
                if (g.Character == character) return g.Lines;
            var lines = new List<Views.ToastLine>();
            byCharacter.Add((character, lines));
            return lines;
        }

        foreach (var colony in PiColonies)
        {
            var headlineParts = new List<string>();

            foreach (var ext in colony.Extractors)
            {
                var inAlert = ext.RefreshCountdown(now, alertHours);
                var key = $"ext:{colony.CharacterId}:{colony.PlanetId}:{ext.ProductTypeId}";
                if (inAlert && _piAlerted.Add(key))
                {
                    LinesFor(colony.CharacterName).Add(new Views.ToastLine(
                        $"{ext.ProductName} extractor {ext.CountdownText}", colony.Title));
                    alertCount++;
                }
                else if (!inAlert)
                    _piAlerted.Remove(key);
            }

            if (colony.Extractors.Count > 0)
            {
                var soonest = colony.Extractors.Where(e => e.ExpiryTime is not null)
                    .OrderBy(e => e.ExpiryTime).FirstOrDefault();
                if (soonest is not null) headlineParts.Add($"next: {soonest.CountdownText}");
            }

            var worst = colony.WorstFillPercent;
            var storeKey = $"store:{colony.CharacterId}:{colony.PlanetId}";
            if (worst >= storagePct && _piAlerted.Add(storeKey))
            {
                LinesFor(colony.CharacterName).Add(new Views.ToastLine(
                    $"Storage {worst:F0}% full", colony.Title));
                alertCount++;
            }
            else if (worst < storagePct)
                _piAlerted.Remove(storeKey);
            if (colony.Storages.Count > 0 || colony.Factories.Count > 0) headlineParts.Add($"storage {worst:F0}%");

            colony.Headline = string.Join("  ·  ", headlineParts);
        }

        if (alertCount > 0) RaisePiAlerts(byCharacter, alertCount);
    }

    private static string PiColonyKey(PiColony c) => $"{c.CharacterId}:{c.PlanetId}";

    // Bundles every colony alert raised in one refresh pass into a single toast + sound, grouped per
    // character (name as a header, its planets as rows beneath). A multi-character empire can raise a
    // dozen at once; the card scrolls past BodyMaxHeightDip rather than growing down the screen.
    private void RaisePiAlerts(List<(string Character, List<Views.ToastLine> Lines)> byCharacter, int alertCount)
    {
        var groups = byCharacter
            .Select(g => new Views.ToastGroup(g.Character, g.Lines))
            .ToList();

        foreach (var g in groups)
            foreach (var l in g.Lines)
                Log.Info($"PI alert — {g.Header}: {l.Primary} ({l.Secondary})");

        SystemSounds.Exclamation.Play();
        var title = alertCount == 1 ? "Planetary Industry" : $"Planetary Industry ({alertCount} alerts)";
        ShowToast(title, groups, "#F59E0B");
    }

    // ── Factory-load calculator ───────────────────────────────────────────────

    private string _piCalcSummary = "";
    public string PiCalcSummary { get => _piCalcSummary; set => SetProperty(ref _piCalcSummary, value); }

    private string _piFactoryInputEntry = "";
    public string PiFactoryInputEntry { get => _piFactoryInputEntry; set => SetProperty(ref _piFactoryInputEntry, value); }

    public int PiFactoryCount
    {
        get => _settings.PiFactoryCount;
        set { if (_settings.PiFactoryCount == value) return; _settings.PiFactoryCount = Math.Max(1, value); OnPropertyChanged(); Save(); }
    }

    public double PiFactoryBurnPerHour
    {
        get => _settings.PiFactoryBurnPerHour;
        set { if (_settings.PiFactoryBurnPerHour == value) return; _settings.PiFactoryBurnPerHour = value <= 0 ? 240 : value; OnPropertyChanged(); Save(); }
    }

    // The character whose assets the calculator totals against — typically whoever consolidates
    // hauled PI materials from the extractor alts. Null = sum every linked character (the old
    // behavior, which can double-count material still sitting on an alt that hasn't been hauled in).
    public List<PiCharacterOption> PiConsolidationOptions =>
        new[] { new PiCharacterOption(null, "All linked characters (sum)") }
            .Concat(Assignments.SelectMany(a => a.EsiCharacters)
                .Select(c => new PiCharacterOption(c.CharacterId, c.CharacterName)))
            .ToList();

    // Bound via SelectedItem (not SelectedValue/SelectedValuePath — PiCharacterOption is a record, and
    // WPF fell back to its auto-generated ToString() in the closed combo box instead of respecting
    // DisplayMemberPath when SelectedValuePath was in play). SelectedItem sidesteps that entirely.
    public PiCharacterOption PiConsolidationSelection
    {
        get => PiConsolidationOptions.FirstOrDefault(o => o.CharacterId == _settings.PiConsolidationCharacterId)
            ?? PiConsolidationOptions[0];
        set
        {
            var newId = value?.CharacterId;
            if (_settings.PiConsolidationCharacterId == newId) return;
            _settings.PiConsolidationCharacterId = newId;
            OnPropertyChanged();
            Save();
            _ = RunFactoryCalcAsync();
        }
    }

    // Reconciles PiFactoryInputs against the union of auto-discovered factory inputs (any tier — P1s
    // feeding Advanced facilities, P2s feeding High-Tech ones, etc., from colony schematics) and
    // manually-added type ids, adding/removing rows as that set changes. Called after every colony poll.
    private async Task SyncFactoryInputsAsync(CancellationToken ct)
    {
        EnsurePiServices();
        var discovered = _piTypes!.GetDiscoveredFactoryInputTypeIds();
        var manual = _settings.PiFactoryInputTypeIds.ToList();
        var wanted = discovered.Concat(manual).Distinct().ToList();

        for (var i = PiFactoryInputs.Count - 1; i >= 0; i--)
            if (!wanted.Contains(PiFactoryInputs[i].TypeId))
                PiFactoryInputs.RemoveAt(i);

        foreach (var id in wanted)
        {
            if (PiFactoryInputs.Any(i => i.TypeId == id)) continue;
            var input = new PiFactoryInput
            {
                TypeId = id,
                Name = $"Type {id}",
                IsAuto = discovered.Contains(id) && !manual.Contains(id),
                IncludeInSplit = !_settings.PiFactoryExcludedInputTypeIds.Contains(id),
            };
            input.PropertyChanged += OnFactoryInputPropertyChanged;
            PiFactoryInputs.Add(input);
            try { input.Name = (await _piTypes.GetTypeAsync(id, ct)).Name; } catch { }
        }
    }

    private void OnFactoryInputPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PiFactoryInput.IncludeInSplit) || sender is not PiFactoryInput input) return;
        if (input.IncludeInSplit) _settings.PiFactoryExcludedInputTypeIds.Remove(input.TypeId);
        else if (!_settings.PiFactoryExcludedInputTypeIds.Contains(input.TypeId)) _settings.PiFactoryExcludedInputTypeIds.Add(input.TypeId);
        Save();
        _ = RunFactoryCalcAsync();
    }

    private async Task AddFactoryInputAsync()
    {
        if (!int.TryParse(PiFactoryInputEntry?.Trim(), out var typeId) || typeId <= 0)
        {
            PiCalcSummary = "Enter a numeric EVE type id to add an input (e.g. 2396 for Biofuels).";
            return;
        }
        if (PiFactoryInputs.Any(i => i.TypeId == typeId)) return;

        var input = new PiFactoryInput
        {
            TypeId = typeId,
            Name = $"Type {typeId}",
            IncludeInSplit = !_settings.PiFactoryExcludedInputTypeIds.Contains(typeId),
        };
        input.PropertyChanged += OnFactoryInputPropertyChanged;
        PiFactoryInputs.Add(input);
        _settings.PiFactoryInputTypeIds.Add(typeId);
        PiFactoryInputEntry = "";
        Save();

        EnsurePiServices();
        try { input.Name = (await _piTypes!.GetTypeAsync(typeId, CancellationToken.None)).Name; } catch { }
        await RunFactoryCalcAsync();
    }

    // Only manually-added inputs can be removed here — auto-discovered ones are hidden from the
    // remove button in XAML, since they'd just reappear on the next poll while the colony still
    // produces them.
    private async void RemoveFactoryInput(object? parameter)
    {
        if (parameter is not PiFactoryInput input) return;
        PiFactoryInputs.Remove(input);
        _settings.PiFactoryInputTypeIds.Remove(input.TypeId);
        _settings.PiFactoryExcludedInputTypeIds.Remove(input.TypeId);
        Save();
        await RunFactoryCalcAsync();
    }

    private async Task RunFactoryCalcAsync()
    {
        if (PiFactoryInputs.Count == 0)
        {
            PiCalcSummary = "No PI materials detected yet — refresh once colonies with a processing facility have been scanned, or add a material manually below.";
            PiCalcRows.Clear();
            return;
        }

        List<long> linked;
        string sourceLabel;
        if (_settings.PiConsolidationCharacterId is long consolidationId && TokenStore.Has(consolidationId))
        {
            linked = new List<long> { consolidationId };
            sourceLabel = Assignments.SelectMany(a => a.EsiCharacters)
                .FirstOrDefault(c => c.CharacterId == consolidationId)?.CharacterName ?? "consolidation character";
        }
        else
        {
            linked = Assignments.SelectMany(a => a.EsiCharacters.Select(c => c.CharacterId))
                .Where(id => TokenStore.Has(id)).Distinct().ToList();
            sourceLabel = "all linked characters (summed)";
        }
        if (linked.Count == 0)
        {
            PiCalcSummary = "No linked characters to total stock from.";
            return;
        }

        PiCalcSummary = "Totalling stock from ESI assets…";
        PiCalcRows.Clear();
        try
        {
            EnsurePiServices();
            var typeIds = PiFactoryInputs.Select(i => i.TypeId).ToList();
            var errors = new List<string>();
            var totals = await _piService!.TotalStockAsync(linked, typeIds, e => errors.Add(e), CancellationToken.None);

            foreach (var input in PiFactoryInputs)
            {
                input.Available = totals.GetValueOrDefault(input.TypeId, 0);
                if (input.Name.StartsWith("Type ", StringComparison.Ordinal))
                    try { input.Name = (await _piTypes!.GetTypeAsync(input.TypeId, CancellationToken.None)).Name; } catch { }
            }

            // Only checked materials gate the "scarcest input" split — an unchecked one (typically an
            // intermediate tier consumed entirely on-planet, see PiFactoryInput.IncludeInSplit) still
            // shows its total above but can't zero out the whole calculation.
            var calcInputs = PiFactoryInputs
                .Where(i => i.IncludeInSplit)
                .Select(i => new FactoryLoadCalculator.Input(i.Name, i.Available))
                .ToList();
            if (calcInputs.Count == 0)
            {
                PiCalcSummary = $"Stock from {sourceLabel}. All materials are unchecked — tick at least one to compute a split.";
                return;
            }
            var result = FactoryLoadCalculator.Compute(calcInputs, PiFactoryCount, PiFactoryBurnPerHour);

            PiCalcRows.Clear();
            if (result.FactoriesGettingExtra > 0)
                PiCalcRows.Add(new PiCalcRow($"{result.FactoriesGettingExtra} factories", result.ExtraQuantity));
            PiCalcRows.Add(new PiCalcRow(
                $"{result.FactoryCount - result.FactoriesGettingExtra} factories", result.BaseQuantity));

            PiCalcSummary =
                $"Stock from {sourceLabel}. Limiting input: {result.LimitingInput} at {result.LimitingAvailable:N0}. " +
                $"Load {result.ExtraQuantity:N0} into {result.FactoriesGettingExtra} factories and " +
                $"{result.BaseQuantity:N0} into {result.FactoryCount - result.FactoriesGettingExtra} — " +
                $"of every input, per factory. Runtime ~{result.RuntimeHours:F1} h.";
            if (errors.Count > 0) PiCalcSummary += $" ({errors.Count} asset error(s).)";
        }
        catch (Exception ex)
        {
            PiCalcSummary = $"Calc failed: {ex.Message}";
        }
    }
}
