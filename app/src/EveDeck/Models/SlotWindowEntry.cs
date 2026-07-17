using System.Text.Json.Serialization;
using EveDeck.Utilities;

namespace EveDeck.Models;

public sealed class SlotWindowEntry : ObservableObject
{
    private string _title = "";
    private int? _lastProcessId;
    private string? _lastHandleHex;

    // Runtime-only: the live window handle this entry last resolved to. NOT persisted -- window
    // handles are meaningless across sessions. FindWindowByEntry pins to this handle while the
    // window is still alive so a Z-order reshuffle (which reorders EnumWindows on every focus
    // change) can't bounce the binding to a same-titled sibling client -- the cause of corner
    // previews "randomly refreshing." Reset to 0 when the window closes.
    [JsonIgnore]
    public nint ResolvedHandle { get; set; }

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
