using System.Collections.ObjectModel;
using EveDeck.Utilities;

namespace EveDeck.Models;

public sealed class SlotAssignment : ObservableObject
{
    private int _slotNumber;
    private string _label = "";
    private string? _frameColor;
    private string? _labelFontFamily;
    private double? _labelFontSize;
    private string? _labelColor;
    private string? _labelFontFamilyMaster;
    private double? _labelFontSizeMaster;
    private string? _labelColorMaster;
    private bool? _labelBold;
    private bool? _labelItalic;
    private bool? _labelDropShadow;
    private bool? _labelOutline;
    private bool? _labelBoldMaster;
    private bool? _labelItalicMaster;
    private bool? _labelDropShadowMaster;
    private bool? _labelOutlineMaster;
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

    // 3b — Optional per-seat preview-label font overrides (null = inherit the global default).
    public string? LabelFontFamily
    {
        get => _labelFontFamily;
        set => SetProperty(ref _labelFontFamily, value);
    }

    public double? LabelFontSize
    {
        get => _labelFontSize;
        set => SetProperty(ref _labelFontSize, value);
    }

    public string? LabelColor
    {
        get => _labelColor;
        set => SetProperty(ref _labelColor, value);
    }

    // 3c — Optional per-seat MASTER-pill font overrides (null = inherit the global Master default,
    // which itself falls back to the normal label font/size/color when unset).
    public string? LabelFontFamilyMaster
    {
        get => _labelFontFamilyMaster;
        set => SetProperty(ref _labelFontFamilyMaster, value);
    }

    public double? LabelFontSizeMaster
    {
        get => _labelFontSizeMaster;
        set => SetProperty(ref _labelFontSizeMaster, value);
    }

    public string? LabelColorMaster
    {
        get => _labelColorMaster;
        set => SetProperty(ref _labelColorMaster, value);
    }

    // 3d — Optional per-seat preview-label STYLE overrides (null = inherit the global default
    // toggle). Mirrors the LabelFontFamily/LabelColor null-means-inherit pattern above exactly.
    public bool? LabelBold
    {
        get => _labelBold;
        set => SetProperty(ref _labelBold, value);
    }

    public bool? LabelItalic
    {
        get => _labelItalic;
        set => SetProperty(ref _labelItalic, value);
    }

    public bool? LabelDropShadow
    {
        get => _labelDropShadow;
        set => SetProperty(ref _labelDropShadow, value);
    }

    public bool? LabelOutline
    {
        get => _labelOutline;
        set => SetProperty(ref _labelOutline, value);
    }

    // 3e — Optional per-seat MASTER-pill STYLE overrides (null = inherit the global Master default,
    // which itself falls back to the normal per-seat/global style toggle when unset).
    public bool? LabelBoldMaster
    {
        get => _labelBoldMaster;
        set => SetProperty(ref _labelBoldMaster, value);
    }

    public bool? LabelItalicMaster
    {
        get => _labelItalicMaster;
        set => SetProperty(ref _labelItalicMaster, value);
    }

    public bool? LabelDropShadowMaster
    {
        get => _labelDropShadowMaster;
        set => SetProperty(ref _labelDropShadowMaster, value);
    }

    public bool? LabelOutlineMaster
    {
        get => _labelOutlineMaster;
        set => SetProperty(ref _labelOutlineMaster, value);
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

    // Protect this seat from bulk-minimize operations (the "Minimize all EVE clients" hotkey and
    // the auto-minimize-inactive option) — e.g. keep a scout or market alt always visible.
    private bool _neverMinimize;
    public bool NeverMinimize
    {
        get => _neverMinimize;
        set => SetProperty(ref _neverMinimize, value);
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

    // The character actually logged into this seat's live window right now. Set by the ViewModel on
    // every refresh from the seat's first DETECTED window (resolved across ALL assigned candidate
    // windows, not just AssignedWindows[0]), so a seat holding several character-set clients shows
    // whichever one is actually running rather than its stable configured main-account Label. Empty
    // when the seat has no live window. Not persisted.
    private string _runningCharacterName = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string RunningCharacterName
    {
        get => _runningCharacterName;
        set
        {
            if (SetProperty(ref _runningCharacterName, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    // Read-only display name for read-only surfaces (minimap, corner-overlay pills): shows the
    // character actually running in the seat when known, else falls back to the seat's stable Label.
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayLabel => string.IsNullOrEmpty(RunningCharacterName) ? Label : RunningCharacterName;

    public string Display => $"{(string.IsNullOrEmpty(PositionCode) ? SlotNumber.ToString() : PositionCode)}. {Label}";
}
