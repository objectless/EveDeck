using EveDeck.Utilities;

namespace EveDeck.Models;

// A non-EVE app whose windows should ALSO show up as detected/assignable windows (Clients tab),
// so one can be previewed in a corner tile alongside EVE clients -- e.g. a spreadsheet or Discord.
// Matched by a case-insensitive substring against the window's owning process name, same convention
// as OverlayAllowedApp. Off by default; the user opts in per app.
public sealed class PreviewableApp : ObservableObject
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
