namespace EveWindowCommander.Models;

public sealed class MonitorPreviewItem
{
    public string Label { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsPrimary { get; set; }
}
