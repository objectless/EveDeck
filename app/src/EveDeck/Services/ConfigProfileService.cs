using System.Reflection;
using System.Text.Json;
using EveDeck.Models;

namespace EveDeck.Services;

// Captures and re-applies the "appearance" slice of AppSettings for a ConfigProfile.
//
// Reflection over a name whitelist rather than a hand-written field-by-field copy: the alternative
// is ~40 assignments duplicated in both directions, which is exactly the kind of list that silently
// falls out of date the first time someone adds a setting and updates only one side.
public static class ConfigProfileService
{
    // Optional log sink, set once by the view-model so failures surface in the Logs tab. Mirrors
    // TileSurfaceWindow.Log -- this is a static helper, and LogService is an instance.
    public static Action<string>? Log;

    // Which AppSettings properties a config profile snapshots. Everything here must be a simple
    // JSON-round-trippable value (string/number/bool) -- deliberately NOT collections like
    // OverlayAllowedApps or Profiles, which are shared state rather than per-config look-and-feel.
    //
    // To add a setting to config profiles: add its property name here. That is the whole change.
    private static readonly string[] AppearanceProperties =
    {
        // Overlay on/off and label content
        nameof(AppSettings.CornerOverlaysEnabled),
        nameof(AppSettings.CornerOverlayShowLabel),
        nameof(AppSettings.CornerOverlayShowSlotNumber),
        nameof(AppSettings.CornerOverlayShowSystem),

        // Label typography and placement
        nameof(AppSettings.CornerOverlayLabelStyle),
        nameof(AppSettings.CornerOverlayLabelFontFamily),
        nameof(AppSettings.CornerOverlayLabelFontSize),
        nameof(AppSettings.CornerOverlayLabelColor),
        nameof(AppSettings.CornerOverlayLabelHeight),
        nameof(AppSettings.CornerOverlayLabelAnchor),
        nameof(AppSettings.CornerOverlayLabelAnchorMaster),
        nameof(AppSettings.CornerOverlayLabelInset),
        nameof(AppSettings.CornerOverlayLabelBold),
        nameof(AppSettings.CornerOverlayLabelItalic),
        nameof(AppSettings.CornerOverlayLabelDropShadow),
        nameof(AppSettings.CornerOverlayLabelOutline),
        nameof(AppSettings.CornerOverlayLabelOpacity),

        // Master-pill overrides
        nameof(AppSettings.CornerOverlayLabelFontFamilyMaster),
        nameof(AppSettings.CornerOverlayLabelFontSizeMaster),
        nameof(AppSettings.CornerOverlayLabelColorMaster),
        nameof(AppSettings.CornerOverlayLabelBoldMaster),
        nameof(AppSettings.CornerOverlayLabelItalicMaster),
        nameof(AppSettings.CornerOverlayLabelDropShadowMaster),
        nameof(AppSettings.CornerOverlayLabelOutlineMaster),
        nameof(AppSettings.CornerOverlayLabelOpacityMaster),

        // Preview tiles
        nameof(AppSettings.CornerOverlayPreviewOpacity),
        nameof(AppSettings.CornerOverlaySnapGridPx),
        nameof(AppSettings.HideActiveSeatTile),
        nameof(AppSettings.HidePreviewsOnFocusLoss),
        nameof(AppSettings.HidePreviewsOnFocusLossDelaySeconds),
        nameof(AppSettings.HidePreviewsAtLoginScreen),
        nameof(AppSettings.HoverZoomAnchor),

        // Active-window frame
        nameof(AppSettings.ActiveFrameEnabled),
        nameof(AppSettings.ActiveFrameThickness),
        nameof(AppSettings.ActiveFrameGlowRadius),
        nameof(AppSettings.ActiveFrameColor),
    };

    private static IEnumerable<PropertyInfo> Properties() =>
        AppearanceProperties
            .Select(name => typeof(AppSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p is not null && p.CanRead && p.CanWrite)!;

    // Snapshots the current appearance settings into `profile`, replacing whatever it held.
    public static void Capture(ConfigProfile profile, AppSettings settings)
    {
        profile.Appearance.Clear();
        foreach (var property in Properties())
        {
            try { profile.Appearance[property!.Name] = JsonSerializer.Serialize(property.GetValue(settings)); }
            catch (Exception ex) { Log?.Invoke($"Config profile capture skipped {property!.Name}: {ex}"); }
        }
    }

    // Writes the profile's stored appearance back onto `settings`. Silently skips any key that no
    // longer maps to a property (a setting removed in a later version) or that will not deserialise
    // -- a stale or hand-edited profile must never stop the app from switching.
    //
    // Returns the number of settings actually applied, so the caller can log something meaningful
    // when a profile turns out to be empty or entirely stale.
    public static int ApplyAppearance(ConfigProfile profile, AppSettings settings)
    {
        var applied = 0;
        foreach (var property in Properties())
        {
            if (!profile.Appearance.TryGetValue(property!.Name, out var json)) continue;
            try
            {
                property.SetValue(settings, JsonSerializer.Deserialize(json, property.PropertyType));
                applied++;
            }
            catch (Exception ex) { Log?.Invoke($"Config profile apply skipped {property.Name}: {ex}"); }
        }
        return applied;
    }
}
