using CommunityToolkit.WinUI.Notifications;

namespace EveDeck.Services;

// Mirrors EveDeck's own styled toast popups (ToastNotificationWindow) into the REAL Windows
// Notification Center -- the flyout opened by clicking the system clock -- so an alert is still
// reviewable after EveDeck's own popup has faded. Every call uses SuppressPopup: Windows never shows
// its own banner for these, EveDeck's styled popup stays the only visible one on screen. This is
// purely a history mirror, not a second notification channel.
//
// Best-effort throughout: this depends on OS notification plumbing EveDeck doesn't control (implicit
// AUMID registration for an unpackaged app, whether the user has notifications disabled for EveDeck
// in Windows Settings, Focus Assist, etc). A failure here must never take down the primary toast
// pipeline -- same defensive posture as ProtocolHandlerService's registry self-heal in App.xaml.cs.
public static class NativeNotificationService
{
    // Wires up click routing for the native copies. `onActivated` receives whatever string was
    // passed to Show's `argument` parameter, or null for a click with no argument. Call once at
    // startup (App.xaml.cs) -- OnActivated is a static event on the compat manager, not tied to any
    // one notification instance.
    public static void Initialize(Action<string?> onActivated)
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += e => onActivated(e.Argument);
        }
        catch
        {
            // Best-effort OS integration -- see class doc comment.
        }
    }

    // Shows a silent (SuppressPopup) native toast carrying `title`/`message`. `argument`, when given,
    // is handed back verbatim to Initialize's callback if the user clicks this notification in Action
    // Center later -- e.g. a mumble:// join link or a marker to bring EveDeck's window to front.
    public static void Show(string title, string message, string? argument = null)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);
            if (!string.IsNullOrEmpty(argument))
                builder.AddArgument("payload", argument);
            builder.Show(toast => toast.SuppressPopup = true);
        }
        catch
        {
            // Best-effort OS integration -- see class doc comment.
        }
    }
}
