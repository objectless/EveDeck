namespace EveWindowCommander.Models;

// Lightweight identity for the hotkey character picker: a seat label plus the main character's
// rounded portrait (empty when the seat has no ESI link). Built fresh by the view-model from the
// current seat assignments; not persisted.
public sealed record CharacterIdentity(string Name, string PortraitUrl)
{
    public bool HasPortrait => !string.IsNullOrEmpty(PortraitUrl);
}
