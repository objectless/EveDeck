using EveDeck.Utilities;

namespace EveDeck.Models;

// A third-party app allowed to visually sit above EveDeck's corner-overlay tile/pill surfaces
// even while an EVE client has focus (e.g. voice chat, intel tools the user keeps positioned
// over the game). Matched by a case-insensitive substring against the window's owning process
// name (Process.ProcessName, which excludes the ".exe" extension).
public sealed class OverlayAllowedApp : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _processName = "";
    public string ProcessName
    {
        get => _processName;
        set => SetProperty(ref _processName, value);
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }
}
