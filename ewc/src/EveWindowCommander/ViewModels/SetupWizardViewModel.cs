using System.Collections.ObjectModel;
using EveWindowCommander.Models;
using EveWindowCommander.Services;
using EveWindowCommander.Utilities;

namespace EveWindowCommander.ViewModels;

// Drives the first-run setup wizard: client count → target monitor → seat/ESI assignment → summary.
// Self-contained so it can be shown modally without touching the main view-model until Finish.
public sealed class SetupWizardViewModel : ObservableObject
{
    private const double Ratio16By9 = 16.0 / 9.0;

    private readonly EsiAuthService _esiAuth = new();
    private bool _esiLoginInProgress;

    public ObservableCollection<int> ClientCountOptions { get; } = new(Enumerable.Range(1, 50));
    public ObservableCollection<MonitorInfo> Monitors { get; }
    public ObservableCollection<SlotAssignment> WizardSlots { get; } = new();

    public RelayCommand AddWizardEsiCharacterCommand { get; }
    public RelayCommand RemoveWizardEsiCharacterCommand { get; }

    public SetupWizardViewModel(IEnumerable<MonitorInfo> monitors)
    {
        Monitors = new ObservableCollection<MonitorInfo>(monitors);
        _selectedMonitor = Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
        AddWizardEsiCharacterCommand = new RelayCommand(AddWizardEsiCharacter);
        RemoveWizardEsiCharacterCommand = new RelayCommand(RemoveWizardEsiCharacter);
    }

    // ── Step navigation ──────────────────────────────────────────────────────────

    private int _step;
    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                if (value == 2) PopulateWizardSlots();
                RaiseStepDependents();
            }
        }
    }

    public bool IsClientStep => Step == 0;
    public bool IsMonitorStep => Step == 1;
    public bool IsEsiStep => Step == 2;
    public bool IsSummaryStep => Step == 3;
    public bool IsLastStep => Step == 3;
    public bool CanGoBack => Step > 0;
    public bool CanGoNext => Step != 1 || SelectedMonitor is not null;
    public string NextButtonText => IsLastStep ? "Finish" : "Next";
    public string StepIndicator => $"Step {Step + 1} of 4";

    public string StepTitle => Step switch
    {
        0 => "Welcome to EveDeck",
        1 => "Choose your display",
        2 => "Link your characters",
        3 => "Ready to apply",
        _ => "Setup"
    };

    public void Next() { if (Step < 3 && CanGoNext) Step++; }
    public void Back() { if (Step > 0) Step--; }

    private void RaiseStepDependents()
    {
        OnPropertyChanged(nameof(IsClientStep));
        OnPropertyChanged(nameof(IsMonitorStep));
        OnPropertyChanged(nameof(IsEsiStep));
        OnPropertyChanged(nameof(IsSummaryStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(StepIndicator));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(SummaryText));
    }

    // The wizard slot that received the FIRST linked character — becomes the app master at finish.
    // 0 = no characters linked yet (the main view-model falls back to its layout default).
    public int MasterSeatNumber { get; private set; }

    private void UpdateMasterFlags()
    {
        foreach (var s in WizardSlots)
            s.IsMaster = s.SlotNumber == MasterSeatNumber;
    }

    // ── Choices ──────────────────────────────────────────────────────────────────

    private int _clientCount = 5;
    public int ClientCount
    {
        get => _clientCount;
        set
        {
            if (!SetProperty(ref _clientCount, value)) return;
            OnPropertyChanged(nameof(IsCenterMaster));
            OnPropertyChanged(nameof(ClientCountDescription));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(MonitorWarning));
            OnPropertyChanged(nameof(HasMonitorWarning));
        }
    }

    private MonitorInfo? _selectedMonitor;
    public MonitorInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (!SetProperty(ref _selectedMonitor, value)) return;
            OnPropertyChanged(nameof(MonitorWarning));
            OnPropertyChanged(nameof(HasMonitorWarning));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(CanGoNext));
        }
    }

    private bool _useWgc = true;
    public bool UseWgc
    {
        get => _useWgc;
        set => SetProperty(ref _useWgc, value);
    }

    private bool _focusPreviewOnClick = true;
    public bool FocusPreviewOnClick
    {
        get => _focusPreviewOnClick;
        set { if (SetProperty(ref _focusPreviewOnClick, value)) OnPropertyChanged(nameof(SummaryText)); }
    }

    public bool IsCenterMaster => ClientCount >= 4 && ClientCount <= 15;

    public bool NoMonitors => Monitors.Count == 0;

    public string ClientCountDescription
    {
        get
        {
            if (ClientCount == 1) return "A single client filling the chosen monitor.";
            if (IsCenterMaster) return ClientCount.ToString() + " clients: a ring of corner/edge tiles with a larger master client floating centered on top. Corner-overlay layout with fast hotkey swapping.";
            if (ClientCount <= 15) return ClientCount.ToString() + " clients arranged in an even grid across the monitor.";
            return ClientCount.ToString() + " clients. Each slot maps to one EVE account. Use the Layouts tab to design a custom grid.";
        }
    }

    public string MonitorWarning
    {
        get
        {
            var m = SelectedMonitor;
            if (m is null) return "";

            var w = m.Bounds.Width;
            var h = m.Bounds.Height;
            if (w <= 0 || h <= 0) return "";

            var aspect = (double)w / h;
            var notWidescreen = Math.Abs(aspect - Ratio16By9) > 0.06;

            var parts = new List<string>();
            if (notWidescreen)
            {
                parts.Add(IsCenterMaster
                    ? $"This display is {w}×{h} ({RatioText(w, h)}), not 16:9. The corner-master layout assumes 16:9 — tiles will be stretched to fill and won't be exactly 16:9. You can still proceed, or pick a grid layout instead."
                    : $"This display is {w}×{h} ({RatioText(w, h)}). Grid tiles will be sized to fit the monitor, which is fine at any aspect ratio.");
            }
            if (m.ScalePercent != 100)
                parts.Add($"Display scaling is {m.ScalePercent:0}%. EWC places windows in physical pixels, so the layout already accounts for this.");

            return string.Join("\n\n", parts);
        }
    }

    public bool HasMonitorWarning => !string.IsNullOrEmpty(MonitorWarning);

    public string SummaryText
    {
        get
        {
            var m = SelectedMonitor;
            var monLabel = m is null ? "the primary monitor" : $"{m.DeviceName} ({m.Bounds.Width}×{m.Bounds.Height})";
            var w = m?.Bounds.Width ?? 2560;
            var h = m?.Bounds.Height ?? 1440;
            var profile = PresetFactory.BestProfileName(ClientCount, w, h) ?? "a built-in layout";

            var lines = new List<string>
            {
                $"• {ClientCount} EVE client{(ClientCount == 1 ? "" : "s")} on {monLabel}",
                $"• Layout preset: {profile}",
            };
            if (IsCenterMaster)
            {
                lines.Add($"• Corner-overlay mode ON, master = center slot {ClientCount}");
                lines.Add($"• High-quality GPU previews: {(UseWgc ? "on" : "off")}");
                lines.Add($"• Click a preview to center that client: {(FocusPreviewOnClick ? "on" : "off")}");
            }

            var totalChars = WizardSlots.Sum(s => s.EsiCharacters.Count);
            var assignedSeats = WizardSlots.Count(s => s.EsiCharacters.Count > 0);
            lines.Add(totalChars > 0
                ? $"• ESI characters linked: {totalChars} across {assignedSeats} slot(s)"
                : "• ESI characters: none linked — add them via the Clients tab after finishing");

            var masterSlot = WizardSlots.FirstOrDefault(s => s.SlotNumber == MasterSeatNumber);
            if (masterSlot is not null)
                lines.Add($"• Master account (centred at rest): {masterSlot.Label}");

            lines.Add("");
            lines.Add("After finishing, assign your running EVE clients to seats in the Clients tab, then Apply (Ctrl+Alt+A).");
            return string.Join("\n", lines);
        }
    }

    // ── Wizard seat slots ────────────────────────────────────────────────────────

    private void PopulateWizardSlots()
    {
        // Trim excess slots if the user changed client count and went Back
        while (WizardSlots.Count > ClientCount)
            WizardSlots.RemoveAt(WizardSlots.Count - 1);
        // Add missing slots
        while (WizardSlots.Count < ClientCount)
            WizardSlots.Add(new SlotAssignment { SlotNumber = WizardSlots.Count + 1, Label = $"Slot {WizardSlots.Count + 1}" });
    }

    private async void AddWizardEsiCharacter(object? parameter)
    {
        if (parameter is not SlotAssignment slot) return;
        if (slot.EsiCharacters.Count >= 3) return;
        if (_esiLoginInProgress) return;

        _esiLoginInProgress = true;
        try
        {
            var (characterId, characterName) = await _esiAuth.AuthorizeAsync(CancellationToken.None);

            if (WizardSlots.Any(s => s.EsiCharacters.Any(c => c.CharacterId == characterId))) return;

            // First character linked anywhere designates this slot as the app master.
            var isFirstEver = WizardSlots.Sum(s => s.EsiCharacters.Count) == 0;

            slot.EsiCharacters.Add(new EsiCharacter { CharacterId = characterId, CharacterName = characterName });
            if (slot.EsiCharacters.Count == 1) slot.Label = characterName;

            if (isFirstEver)
            {
                MasterSeatNumber = slot.SlotNumber;
                UpdateMasterFlags();
            }
            OnPropertyChanged(nameof(SummaryText));
        }
        catch { /* login cancelled or failed — silently ignore in wizard */ }
        finally { _esiLoginInProgress = false; }
    }

    private void RemoveWizardEsiCharacter(object? parameter)
    {
        if (parameter is not EsiCharacter character) return;
        var slot = WizardSlots.FirstOrDefault(s => s.EsiCharacters.Contains(character));
        if (slot is null) return;
        slot.EsiCharacters.Remove(character);
        if (slot.EsiCharacters.Count > 0 && slot.Label.Equals(character.CharacterName, StringComparison.OrdinalIgnoreCase))
            slot.Label = slot.EsiCharacters[0].CharacterName;
        else if (slot.EsiCharacters.Count == 0)
            slot.Label = $"Slot {slot.SlotNumber}";

        // If the master slot was emptied, hand the master badge to the next slot that still has a character.
        if (MasterSeatNumber == slot.SlotNumber && slot.EsiCharacters.Count == 0)
        {
            MasterSeatNumber = WizardSlots.FirstOrDefault(s => s.EsiCharacters.Count > 0)?.SlotNumber ?? 0;
            UpdateMasterFlags();
        }
        OnPropertyChanged(nameof(SummaryText));
    }

    private static string RatioText(int w, int h)
    {
        var g = Gcd(w, h);
        return g == 0 ? $"{w}:{h}" : $"{w / g}:{h / g}";
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { (a, b) = (b, a % b); }
        return Math.Abs(a);
    }
}
