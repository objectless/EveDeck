using EveDeck.Utilities;

namespace EveDeck.Models;

public sealed class SwapGroup : ObservableObject
{
    private string _name = "Group";

    public string GroupId { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    // Profile slot numbers (position IDs) that belong to this group. Empty = all slots (legacy single-group).
    public List<int> SlotNumbers { get; set; } = new();
}
