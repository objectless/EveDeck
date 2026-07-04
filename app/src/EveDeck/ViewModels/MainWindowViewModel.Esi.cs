using EveDeck.Models;
using EveDeck.Services;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly EsiAuthService _esiAuth = new();
    private bool _esiLoginInProgress;

    private async void AddEsiCharacter(object? parameter)
    {
        if (parameter is not SlotAssignment slot) return;

        if (slot.EsiCharacters.Count >= 3)
        {
            Log.Warn("A seat can hold at most 3 characters.");
            return;
        }
        if (_esiLoginInProgress)
        {
            Log.Warn("An ESI login is already in progress — check your browser.");
            return;
        }

        _esiLoginInProgress = true;
        try
        {
            Log.Info($"Opening EVE SSO login for seat {slot.SlotNumber} — sign in and authorise in your browser.");
            var (characterId, characterName) = await _esiAuth.AuthorizeAsync(CancellationToken.None);

            if (Assignments.Any(a => a.EsiCharacters.Any(c => c.CharacterId == characterId)))
            {
                Log.Warn($"{characterName} is already assigned to a seat.");
                return;
            }

            // The first character linked anywhere becomes the app master (its name + portrait brand
            // the title bar). Detect BEFORE adding so we only promote on a truly empty roster.
            var isFirstEver = Assignments.Sum(a => a.EsiCharacters.Count) == 0;

            slot.EsiCharacters.Add(new EsiCharacter { CharacterId = characterId, CharacterName = characterName });

            if (slot.EsiCharacters.Count == 1)
                slot.Label = characterName;

            var windowTitle = $"EVE - {characterName}";
            if (!slot.AssignedWindows.Any(w => w.Title.Equals(windowTitle, StringComparison.OrdinalIgnoreCase)))
                slot.AssignedWindows.Add(new SlotWindowEntry { Title = windowTitle });

            Log.Info($"Added {characterName} (ID {characterId}) to seat {slot.SlotNumber} ({slot.Label}).");

            if (isFirstEver)
            {
                SetMasterSlot(slot);
                Log.Info($"{characterName} is the first linked character - set as app master.");
            }

            Save();
            RaiseIdentityDependents();
        }
        catch (OperationCanceledException)
        {
            Log.Info("ESI login cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error($"ESI login failed: {ex.Message}");
        }
        finally
        {
            _esiLoginInProgress = false;
        }
    }

    private void RemoveEsiCharacter(object? parameter)
    {
        if (parameter is not EsiCharacter character) return;

        var slot = Assignments.FirstOrDefault(a => a.EsiCharacters.Contains(character));
        if (slot is null) return;

        slot.EsiCharacters.Remove(character);

        var windowTitle = $"EVE - {character.CharacterName}";
        var entry = slot.AssignedWindows.FirstOrDefault(w => w.Title.Equals(windowTitle, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) slot.AssignedWindows.Remove(entry);

        if (slot.EsiCharacters.Count > 0 && slot.Label.Equals(character.CharacterName, StringComparison.OrdinalIgnoreCase))
            slot.Label = slot.EsiCharacters[0].CharacterName;

        Log.Info($"Removed {character.CharacterName} from seat {slot.SlotNumber}.");
        Save();
        RaiseIdentityDependents();
    }
}
