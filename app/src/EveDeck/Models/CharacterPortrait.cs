using System.Windows.Media;
using EveDeck.Utilities;

namespace EveDeck.Models;

// One character's cached portrait as an observable ImageSource. PortraitCacheService hands out a
// single shared instance per character id, so every surface bound to it (title bar, seat roster,
// corner-overlay labels, pickers) updates together the moment a fresh portrait finishes downloading
// or is re-fetched. The Image starts null and is filled in on the UI thread once the on-disk cache
// has the bytes.
public sealed class CharacterPortrait : ObservableObject
{
    private ImageSource? _image;

    public CharacterPortrait(long characterId) => CharacterId = characterId;

    public long CharacterId { get; }

    public ImageSource? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value)) return;
            _image = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImage));
        }
    }

    public bool HasImage => _image is not null;
}
