namespace EveWindowCommander.Models;

public sealed class MonitorInfo
{
    public string Id { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public WindowRect Bounds { get; set; } = new();
    public WindowRect WorkArea { get; set; } = new();
    public bool IsPrimary { get; set; }
    public uint DpiX { get; set; } = 96;
    public uint DpiY { get; set; } = 96;
    public double ScalePercent => DpiX <= 0 ? 100 : Math.Round(DpiX / 96.0 * 100, 0);
    public string Summary => $"{DeviceName} {Bounds} work {WorkArea} scale {ScalePercent}%";

    // WPF ComboBox selection boxes fall back to ToString() in some template paths even with
    // DisplayMemberPath set - show the summary instead of the type name.
    public override string ToString() => Summary;
}
