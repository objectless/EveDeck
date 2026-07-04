using EveDeck.Utilities;

namespace EveDeck.Models;

public sealed class SlotWindowEntry : ObservableObject
{
    private string _title = "";
    private int? _lastProcessId;
    private string? _lastHandleHex;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public int? LastProcessId
    {
        get => _lastProcessId;
        set => SetProperty(ref _lastProcessId, value);
    }

    public string? LastHandleHex
    {
        get => _lastHandleHex;
        set => SetProperty(ref _lastHandleHex, value);
    }
}
