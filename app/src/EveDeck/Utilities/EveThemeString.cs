using System.Text.RegularExpressions;

namespace EveDeck.Utilities;

// Parses the shareable colour string EVE Online's Photon UI "Edit Theme" dialog exports via its
// copy button: "#RRGGBB,#RRGGBB,#RRGGBB,#RRGGBB" = Primary,Accent,Background Tint,Alert (in that
// order). Only Primary and Accent are used -- EveDeck maps Primary to a seat's frame colour and
// Accent to its label colour so a character's whole look matches their in-game theme in one paste.
public static class EveThemeString
{
    private static readonly Regex HexColor = new(@"^#?[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    public static bool TryParse(string? raw, out string primary, out string accent)
    {
        primary = "";
        accent = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !HexColor.IsMatch(parts[0]) || !HexColor.IsMatch(parts[1])) return false;

        primary = Normalize(parts[0]);
        accent = Normalize(parts[1]);
        return true;
    }

    private static string Normalize(string hex) => "#" + hex.TrimStart('#').ToUpperInvariant();
}
