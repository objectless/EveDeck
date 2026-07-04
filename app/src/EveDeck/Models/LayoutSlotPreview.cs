namespace EveDeck.Models;

public sealed class LayoutSlotPreview
{
    public int SlotNumber { get; set; }
    public string DisplayText { get; set; } = "";
    public string Label { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
