using System.Collections.ObjectModel;
using EveWindowCommander.Utilities;

namespace EveWindowCommander.Models;

public sealed class CharacterSet : ObservableObject
{
    private bool _isActive;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _name = "Default";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    // True while this set is loaded into the live Assignments/Hotkeys collections.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public ObservableCollection<SlotAssignment> Assignments { get; set; } = new();
    public ObservableCollection<HotkeyBinding> Hotkeys { get; set; } = new();
}
