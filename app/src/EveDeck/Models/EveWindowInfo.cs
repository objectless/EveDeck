namespace EveDeck.Models;

public sealed class EveWindowInfo
{
    public string Title { get; set; } = "";
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public nint Handle { get; set; }
    public string HandleHex => $"0x{Handle.ToInt64():X}";
    public WindowRect Rect { get; set; } = new();
    public string MonitorId { get; set; } = "";
    public bool IsBorderless { get; set; }
    public string DisplayName => $"{Title} ({ProcessId})";
}
