namespace EveDeck.Models;

// Lightweight identity for the hotkey character picker: a seat label plus the main character's
// cache-backed portrait (null when the seat has no ESI link). Built fresh by the view-model from the
// current seat assignments; not persisted.
public sealed record CharacterIdentity(string Name, CharacterPortrait? Portrait)
{
    public bool HasPortrait => Portrait is not null;
}
