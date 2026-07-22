using System.Collections.ObjectModel;
using EveDeck.Services;
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
    private string? _labelAnchor;
    private string? _labelAnchorMaster;
    private string? _labelAlias;
    private string? _zoomAnchor;
    private bool? _labelBold;
    private bool? _labelItalic;
    private bool? _labelDropShadow;
    private bool? _labelOutline;
    private bool? _labelBoldMaster;
    private bool? _labelItalicMaster;
    private bool? _labelDropShadowMaster;
    private bool? _labelOutlineMaster;
    private int? _labelOpacity;
    private int? _labelOpacityMaster;
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
        OnPropertyChanged(nameof(RunningPortrait));
    }

    private void RaiseCharacterDependents()
    {
        OnPropertyChanged(nameof(MainCharacter));
        OnPropertyChanged(nameof(PortraitUrl));
        OnPropertyChanged(nameof(HasPortrait));
        OnPropertyChanged(nameof(RunningPortrait));
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

    // Optional per-seat label ANCHOR overrides -- where this seat's label sits within its tile, as
    // one of the nine 3x3 names (TopLeft ... BottomRight; see AppSettings.CornerOverlayLabelAnchor).
    // null/empty = inherit. Two separate values because a seat looks different in the two roles it
    // occupies: its small corner tile usually wants the name centered over the thumbnail, while the
    // same seat centered as MASTER wants it out of the way at the top. Same seat-override ->
    // global-master -> global-default chain as the font overrides below.
    public string? LabelAnchor
    {
        get => _labelAnchor;
        set => SetProperty(ref _labelAnchor, value);
    }

    public string? LabelAnchorMaster
    {
        get => _labelAnchorMaster;
        set => SetProperty(ref _labelAnchorMaster, value);
    }

    // Optional per-seat hover-zoom anchor (one of the nine 3x3 names). null/empty = inherit
    // AppSettings.HoverZoomAnchor. Useful when one tile sits hard against a screen edge and needs to
    // grow inward while the rest grow from their center.
    public string? ZoomAnchor
    {
        get => _zoomAnchor;
        set => SetProperty(ref _zoomAnchor, value);
    }

    // Display name shown on this seat's preview label INSTEAD of the running character name.
    // Null/blank = show the real character name as before. Mirrors EVE-O Preview's label aliases --
    // purely cosmetic, and deliberately does not affect matching, assignment, or anything the app
    // does with the real name.
    public string? LabelAlias
    {
        get => _labelAlias;
        set { if (SetProperty(ref _labelAlias, value)) OnPropertyChanged(nameof(DisplayLabel)); }
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

    // 3f — Optional per-seat label OPACITY overrides (0-100%; null = inherit the global default /
    // global Master default, same fallback pattern as the style toggles above).
    public int? LabelOpacity
    {
        get => _labelOpacity;
        set => SetProperty(ref _labelOpacity, value);
    }

    public int? LabelOpacityMaster
    {
        get => _labelOpacityMaster;
        set => SetProperty(ref _labelOpacityMaster, value);
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

    // When set, activating this seat (tile click, hotkey, protocol) just brings its window to the
    // foreground in place instead of swapping it into the master/center region. Also suppresses the
    // hover-peek master swap for this seat. Mirrors ISBoxer's per-slot "swap to main: Never".
    private bool _focusOnlyNoSwap;
    public bool FocusOnlyNoSwap
    {
        get => _focusOnlyNoSwap;
        set => SetProperty(ref _focusOnlyNoSwap, value);
    }

    // Skip this seat's live preview entirely (no DWM/WGC capture registered) and just show the
    // tile's plain fill colour instead -- e.g. for a cloaky alt you don't want visible even as a
    // thumbnail. Mirrors EVE-O Preview's per-client DisableThumbnail / PreventPreviewColor.
    private bool _preventPreview;
    public bool PreventPreview
    {
        get => _preventPreview;
        set => SetProperty(ref _preventPreview, value);
    }

    // Skip this seat when cycling (Cycle/CycleGroup hotkeys) without unassigning it. Toggled via
    // Shift+click on the seat's corner tile — mirrors EVE-O Preview's shift+click cycle-group toggle.
    private bool _excludedFromCycle;
    public bool ExcludedFromCycle
    {
        get => _excludedFromCycle;
        set => SetProperty(ref _excludedFromCycle, value);
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

    // Cache-backed portrait of the character ACTUALLY running in this seat right now (read-only
    // surfaces: corner-overlay labels, title-bar master). Prefers a linked ESI character (id already
    // known), else resolves the live window's character name -> id via ESI, and falls back to the
    // seat's main character while nothing is running or the name is still resolving -- so a label
    // always shows a face rather than blanking. Not persisted.
    [System.Text.Json.Serialization.JsonIgnore]
    public CharacterPortrait? RunningPortrait
    {
        get
        {
            var running = _runningCharacterName;
            if (!string.IsNullOrWhiteSpace(running))
            {
                var linked = _esiCharacters.FirstOrDefault(c =>
                    running.Equals(c.CharacterName, StringComparison.OrdinalIgnoreCase));
                if (linked is not null) return PortraitCacheService.Instance.ForId(linked.CharacterId);

                var byName = PortraitCacheService.Instance.ForName(running);
                if (byName is not null) return byName;
            }
            return MainCharacter is not null ? PortraitCacheService.Instance.ForId(MainCharacter.CharacterId) : null;
        }
    }

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
    // Name shown on this seat's preview label and in the layout preview. A LabelAlias wins over both
    // the running character and the configured main -- it is purely cosmetic, and deliberately does
    // NOT feed window matching, assignment or profile sync, all of which use the real names (see
    // project-charset-title-corruption for why conflating a display name with a match key is a bug).
    public string DisplayLabel => !string.IsNullOrWhiteSpace(LabelAlias)
        ? LabelAlias!
        : string.IsNullOrEmpty(RunningCharacterName) ? Label : RunningCharacterName;

    public string Display => $"{(string.IsNullOrEmpty(PositionCode) ? SlotNumber.ToString() : PositionCode)}. {Label}";
}
