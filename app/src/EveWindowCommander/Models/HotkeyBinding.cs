using EveWindowCommander.Utilities;

namespace EveWindowCommander.Models;

public sealed class HotkeyBinding : ObservableObject
{
    private string _actionId = "";
    private string _displayName = "";
    private uint _modifiers;
    private uint _virtualKey;
    private bool _enabled = true;
    private string _gestureText = "";
    private string _targetCharacter = "";

    public string ActionId
    {
        get => _actionId;
        set
        {
            if (SetProperty(ref _actionId, value))
            {
                OnPropertyChanged(nameof(Category));
                OnPropertyChanged(nameof(IsCharacterSwitch));
            }
        }
    }

    // For "Switch to character" actions only: the name (slot label) of the character this
    // hotkey targets. The action resolves the character's CURRENT slot at press time, so the
    // binding follows the character across master swaps rather than pointing at a fixed slot.
    public string TargetCharacter
    {
        get => _targetCharacter;
        set => SetProperty(ref _targetCharacter, value);
    }

    public bool IsCharacterSwitch
        => ActionId.StartsWith("SwitchToCharacter", StringComparison.OrdinalIgnoreCase);

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public uint Modifiers
    {
        get => _modifiers;
        set => SetProperty(ref _modifiers, value);
    }

    public uint VirtualKey
    {
        get => _virtualKey;
        set => SetProperty(ref _virtualKey, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string GestureText
    {
        get => _gestureText;
        set
        {
            if (SetProperty(ref _gestureText, value))
            {
                OnPropertyChanged(nameof(DisplayGesture));
            }
        }
    }

    public string DisplayGesture => string.IsNullOrWhiteSpace(GestureText) ? "Not set" : GestureText;

    private bool _isCapturing;
    public bool IsCapturing
    {
        get => _isCapturing;
        set => SetProperty(ref _isCapturing, value);
    }

    public string Category
    {
        get
        {
            if (ActionId.StartsWith("SwitchToCharacter", StringComparison.OrdinalIgnoreCase)) return "Character";
            if (ActionId.StartsWith("FocusSlot", StringComparison.OrdinalIgnoreCase)) return "Focus";
            if (ActionId.StartsWith("Cycle", StringComparison.OrdinalIgnoreCase)) return "Cycle";
            if (ActionId.Contains("Layout", StringComparison.OrdinalIgnoreCase) || ActionId.Contains("Borderless", StringComparison.OrdinalIgnoreCase) || ActionId.Contains("Style", StringComparison.OrdinalIgnoreCase)) return "Layout / Style";
            if (ActionId.StartsWith("Move", StringComparison.OrdinalIgnoreCase) || ActionId.StartsWith("Swap", StringComparison.OrdinalIgnoreCase)) return "Move / Swap";
            return "Other";
        }
    }
}
