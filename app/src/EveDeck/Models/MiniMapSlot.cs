namespace EveDeck.Models;

public sealed class MiniMapSlot
{
    public int SlotNumber { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string PositionCode { get; set; } = "";
    public string AssignedLabel { get; set; } = "";
    public bool IsAssigned { get; set; }

    // Which seat (SlotAssignment.SlotNumber) occupies this position at rest. For the center cell this
    // is the master seat; for corners it's the home seat. Used when a window/seat is dropped on the cell.
    public int OccupantSeat { get; set; }

    // The center cell (largest rect). Dropping a seat here makes it master; corners set the home corner.
    public bool IsCenter { get; set; }

    // True when this cell currently shows the master seat (always the center cell in grid mode).
    public bool IsMaster { get; set; }
}
