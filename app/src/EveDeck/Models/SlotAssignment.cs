using System.Collections.ObjectModel;
using EveDeck.Utilities;

namespace EveDeck.Models;

public sealed class SlotAssignment : ObservableObject
{
    private int _slotNumber;
    private string _label = "";
    private string? _frameColor;
    private bool _isMaster;
    private string _positionCode = "";

    public SlotAssignment()
    {
        AssignedWindows.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(IsAssigned));
            RaiseRunningNameDependents();
            if (e.OldItems is not null)
                foreach (SlotWindowEntry entry in e.OldItems)
                    entry.PropertyChanged -= OnAssignedWindowEntryChanged;
            if (e.NewItems is not null)
                foreach (SlotWindowEntry entry in e.NewItems)
                    entry.PropertyChanged += OnAssignedWindowEntryChanged;
        };
        _esiCharacters.CollectionChanged += (_, _) => RaiseCharacterDependents();
    }

    private void OnAssignedWindowEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SlotWindowEntry.Title)) RaiseRunningNameDependents();
    }

    private void RaiseRunningNameDependents()
    {
        OnPropertyChanged(nameof(RunningCharacterName));
        OnPropertyChanged(nameof(DisplayLabel));
    }

    private void RaiseCharacterDependents()
    {
        OnPropertyChanged(nameof(MainCharacter));
        OnPropertyChanged(nameof(PortraitUrl));
        OnPropertyChanged(nameof(HasPortrait));
    }

    public int SlotNumber
    {
        get => _slotNumber;
        set
        {
            if (SetProperty(ref _slotNumber, value))
                OnPropertyChanged(nameof(Display));
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            if (SetProperty(ref _label, value))
                OnPropertyChanged(nameof(Display));
        }
    }

    public bool IsMaster
    {
        get => _isMaster;
        set => SetProperty(ref _isMaster, value);
    }

    // Positional code within the active layout (e.g. "Master", arrow symbols for screen position).
    // Computed from slot geometry by the view-model; used in the UI instead of a bare slot number.
    public string PositionCode
    {
        get => _positionCode;
        set
        {
            if (SetProperty(ref _positionCode, value))
                OnPropertyChanged(nameof(Display));
        }
    }

    // 3a — Optional per-slot frame overlay color (null = use global color).
    public string? FrameColor
    {
        get => _frameColor;
        set => SetProperty(ref _frameColor, value);
    }

    // Keep this seat's EVE window pinned topmost (HWND_TOPMOST) at all times.
    private bool _isTopmost;
    public bool IsTopmost
    {
        get => _isTopmost;
        set => SetProperty(ref _isTopmost, value);
    }

    // UI-only drag-drop state — not persisted.
    private bool _isDragSwapTarget;

    public bool IsDragSwapTarget
    {
        get => _isDragSwapTarget;
        set => SetProperty(ref _isDragSwapTarget, value);
    }

    // UI-only chat-alert flash state — not persisted. Set true on a keyword match, auto-reset to
    // false a couple seconds later by the ViewModel (see MainWindowViewModel.ChatAlerts.cs).
    private bool _isAlerting;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAlerting
    {
        get => _isAlerting;
        set => SetProperty(ref _isAlerting, value);
    }

    // Legacy migration fields — kept for backward-compat JSON reading; null after first migration save.
    public string? AssignedWindowTitle { get; set; }
    public int? LastProcessId { get; set; }
    public string? LastHandleHex { get; set; }

    public ObservableCollection<SlotWindowEntry> AssignedWindows { get; set; } = new();

    // ESI-verified identities occupying this seat (up to 3). First entry is the main character.
    // Re-subscribing setter so portrait/name notifications survive JSON deserialization (which
    // replaces the collection instance wholesale).
    private ObservableCollection<EsiCharacter> _esiCharacters = new();
    public ObservableCollection<EsiCharacter> EsiCharacters
    {
        get => _esiCharacters;
        set
        {
            _esiCharacters.CollectionChanged -= OnEsiCharactersChanged;
            _esiCharacters = value ?? new();
            _esiCharacters.CollectionChanged += OnEsiCharactersChanged;
            RaiseCharacterDependents();
        }
    }

    private void OnEsiCharactersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RaiseCharacterDependents();

    // The main (first) ESI character occupying this seat, or null when none is linked.
    [System.Text.Json.Serialization.JsonIgnore]
    public EsiCharacter? MainCharacter => _esiCharacters.Count > 0 ? _esiCharacters[0] : null;

    // Rounded portrait source for this seat (the main character's), empty when unlinked.
    [System.Text.Json.Serialization.JsonIgnore]
    public string PortraitUrl => MainCharacter?.PortraitUrlSmall ?? "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPortrait => MainCharacter is not null;

    public bool IsAssigned => AssignedWindows.Count > 0;

    // The character actually logged into this seat's window right now (from the live "EVE - Name"
    // window title), independent of the seat's stable configured Label. Empty when unassigned.
    [System.Text.Json.Serialization.JsonIgnore]
    public string RunningCharacterName
    {
        get
        {
            if (AssignedWindows.Count == 0) return "";
            const string prefix = "EVE - ";
            var title = AssignedWindows[0].Title;
            return title.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)
                ? title[prefix.Length..].Trim()
                : "";
        }
    }

    // Read-only display name for read-only surfaces (minimap, corner-overlay pills): shows the
    // character actually running in the seat when known, else falls back to the seat's stable Label.
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayLabel => string.IsNullOrEmpty(RunningCharacterName) ? Label : RunningCharacterName;

    public string Display => $"{(string.IsNullOrEmpty(PositionCode) ? SlotNumber.ToString() : PositionCode)}. {Label}";
}
