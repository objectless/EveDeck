using System.Windows;
using MessageBox = System.Windows.MessageBox;
using EveDeck.Models;
using EveDeck.Services;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly EsiAuthService _esiAuth = new();
    private bool _esiLoginInProgress;

    // Encrypted refresh/access token store for the PI monitor. Lazily created so tests that never
    // touch ESI don't write a tokens file. Keyed off the same app-data folder as settings.json.
    private EsiTokenStore? _tokenStore;
    public EsiTokenStore TokenStore => _tokenStore ??= new EsiTokenStore(_configService.AppDataFolder);

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
            var token = await _esiAuth.AuthorizeAsync(CancellationToken.None);
            var characterId = token.CharacterId;
            var characterName = token.CharacterName;

            if (Assignments.Any(a => a.EsiCharacters.Any(c => c.CharacterId == characterId)))
            {
                Log.Warn($"{characterName} is already assigned to a seat.");
                return;
            }

            // Persist the (encrypted) token so the PI monitor can call ESI on this character's behalf.
            TokenStore.Put(token);
            if (!token.HasScope(EsiAuthService.ScopePlanets))
                Log.Warn($"{characterName} was linked without the Planetary Industry scope — the Planets tab won't see their colonies. Re-link and keep all boxes ticked to fix.");
            if (!token.HasScope(EsiAuthService.ScopeSkills))
                Log.Warn($"{characterName} was linked without the skills scope — their colony cards won't show an Interplanetary Consolidation level. Re-link and keep all boxes ticked to fix.");

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

    // Re-runs the SSO login for an already-linked character to refresh its stored ESI token — e.g.
    // to pick up a newly-added scope (like Planetary Industry) without removing and re-adding the
    // character. Does not touch the seat/assignment, only the token in TokenStore.
    private async void ReauthEsiCharacter(object? parameter)
    {
        if (parameter is not EsiCharacter character) return;

        if (_esiLoginInProgress)
        {
            Log.Warn("An ESI login is already in progress — check your browser.");
            return;
        }

        _esiLoginInProgress = true;
        try
        {
            Log.Info($"Opening EVE SSO login to re-authorise {character.CharacterName} — sign in as the SAME character in your browser.");
            var token = await _esiAuth.AuthorizeAsync(CancellationToken.None);

            if (token.CharacterId != character.CharacterId)
            {
                Log.Warn($"Re-auth signed in as {token.CharacterName}, but this seat entry is {character.CharacterName} — ignored. Log in as {character.CharacterName} instead.");
                return;
            }

            TokenStore.Put(token);
            var missing = new List<string>();
            if (!token.HasScope(EsiAuthService.ScopePlanets)) missing.Add("Planetary Industry");
            if (!token.HasScope(EsiAuthService.ScopeSkills)) missing.Add("skills");
            Log.Info(missing.Count == 0
                ? $"Re-authorised {character.CharacterName} — all scopes granted."
                : $"Re-authorised {character.CharacterName}, but the {string.Join(" and ", missing)} scope(s) are still missing — make sure every box is ticked on the SSO consent screen.");
        }
        catch (OperationCanceledException)
        {
            Log.Info("ESI re-auth cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error($"ESI re-auth failed: {ex.Message}");
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

        var result = MessageBox.Show(
            $"Remove {character.CharacterName} from seat {slot.SlotNumber}? You'll need to sign in again via EVE SSO to re-add it.",
            "Remove Character", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        slot.EsiCharacters.Remove(character);

        var windowTitle = $"EVE - {character.CharacterName}";
        var entry = slot.AssignedWindows.FirstOrDefault(w => w.Title.Equals(windowTitle, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) slot.AssignedWindows.Remove(entry);

        if (slot.EsiCharacters.Count > 0 && slot.Label.Equals(character.CharacterName, StringComparison.OrdinalIgnoreCase))
            slot.Label = slot.EsiCharacters[0].CharacterName;

        TokenStore.Remove(character.CharacterId);
        if (_settings.PiConsolidationCharacterId == character.CharacterId)
            _settings.PiConsolidationCharacterId = null;

        Log.Info($"Removed {character.CharacterName} from seat {slot.SlotNumber}.");
        Save();
        RaiseIdentityDependents();
    }
}
