namespace EveDeck.Models;

public sealed class EsiCharacter
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";

    // Public Fenris Creations image server (no auth). WPF BitmapImage downloads + caches these via WinINET,
    // so binding an Image.Source directly to the URL is sufficient — no custom cache needed.
    // Valid sizes: 32, 64, 128, 256, 512.
    public string PortraitUrl => CharacterId > 0
        ? $"https://images.evetech.net/characters/{CharacterId}/portrait?size=128"
        : "";

    public string PortraitUrlSmall => CharacterId > 0
        ? $"https://images.evetech.net/characters/{CharacterId}/portrait?size=64"
        : "";
}
