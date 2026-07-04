using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using EveDeck.Models;

namespace EveDeck.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly bool _isDefaultFolder;

    public string AppDataFolder { get; }

    public ConfigService(string? appDataFolder = null)
    {
        _isDefaultFolder = appDataFolder is null;
        AppDataFolder = appDataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EveDeck");
    }

    public string ConfigPath => Path.Combine(AppDataFolder, "settings.json");
    public string LogsFolder => Path.Combine(AppDataFolder, "logs");
    public string BackupsFolder => Path.Combine(AppDataFolder, "backups");

    public AppSettings Load()
    {
        // One-time migration: move settings from old "EVE Window Commander" folder to "EveDeck".
        var oldFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE Window Commander");
        if (_isDefaultFolder && Directory.Exists(oldFolder) && !Directory.Exists(AppDataFolder))
        {
            try { Directory.Move(oldFolder, AppDataFolder); } catch { } // best-effort migration; LogService not alive during Load
        }

        Directory.CreateDirectory(AppDataFolder);
        Directory.CreateDirectory(LogsFolder);

        // Snapshot settings on each launch so the user always has a restore point.
        if (File.Exists(ConfigPath)) CreateBackup();

        AppSettings? settings = null;
        if (File.Exists(ConfigPath))
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath), JsonOptions);
            }
            catch
            {
                // Corrupt settings file — back it up and start fresh.
                var bakPath = ConfigPath + ".bak";
                try { File.Move(ConfigPath, bakPath, overwrite: true); } catch { } // best-effort backup; LogService not alive during Load
            }
        }

        settings ??= new AppSettings();
        EnsureDefaults(settings);
        Save(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataFolder);
        // Write to a temp file then atomically replace — prevents zero-filled settings.json on crash.
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        // File.Replace requires the destination to already exist (throws FileNotFoundException
        // otherwise) — falls back to a plain move on first-ever launch or right after Load() moves
        // a corrupt settings.json out of the way.
        if (File.Exists(ConfigPath))
            File.Replace(tmp, ConfigPath, destinationBackupFileName: null);
        else
            File.Move(tmp, ConfigPath);
    }

    // ── Backup ────────────────────────────────────────────────────────────────

    private static bool IsFileHealthy(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 10 || bytes.All(b => b == 0)) return false;
            return JsonSerializer.Deserialize<AppSettings>(
                System.Text.Encoding.UTF8.GetString(bytes), new JsonSerializerOptions()) is not null;
        }
        catch { return false; }
    }

    // Copy current settings to a timestamped file in BackupsFolder.
    // Skips if the current settings.json is corrupt/empty to avoid overwriting healthy backups.
    public void CreateBackup()
    {
        if (!File.Exists(ConfigPath)) return;
        var raw = File.ReadAllText(ConfigPath);
        AppSettings? s = null;
        try { s = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions); } catch { } // best-effort backup; LogService not alive during Load
        if (s is null) return; // corrupt — don't overwrite good backups
        Directory.CreateDirectory(BackupsFolder);
        var slots = s.CharacterSets.Sum(cs => cs.Assignments.Count);
        var chars = s.CharacterSets.Sum(cs => cs.Assignments.Sum(a => a.EsiCharacters.Count));
        var now = DateTime.Now;
        var dest = Path.Combine(BackupsFolder, SettingsBackup.BuildFileName(now, slots, chars));
        if (!File.Exists(dest))
            try { File.Copy(ConfigPath, dest); } catch { } // best-effort backup; LogService not alive during Load
        PruneBackups();
    }

    public IReadOnlyList<SettingsBackup> GetBackups()
    {
        if (!Directory.Exists(BackupsFolder)) return Array.Empty<SettingsBackup>();
        return Directory.GetFiles(BackupsFolder, "settings_backup_*.json")
            .Select(p => SettingsBackup.FromPath(p))
            .Where(b => b is not null)
            .Cast<SettingsBackup>()
            .OrderByDescending(b => b.Timestamp)
            .ToList();
    }

    // Overwrite settings.json with a backup file. Throws if the backup is corrupt.
    // Caller is responsible for restarting the app.
    public void RestoreBackup(string backupPath)
    {
        if (!IsFileHealthy(backupPath))
            throw new InvalidOperationException("The selected backup file appears to be corrupt and cannot be restored.");
        File.Copy(backupPath, ConfigPath, overwrite: true);
    }

    // Keep 5 backups from today + 1 per prior day for 7 days (roughly 12 total).
    private void PruneBackups()
    {
        var all = GetBackups();
        var today = DateTime.Today;
        var keep = all
            .Where(b => b.Timestamp.Date == today).Take(5)
            .Concat(all
                .Where(b => b.Timestamp.Date != today)
                .GroupBy(b => b.Timestamp.Date)
                .Where(g => (today - g.Key).TotalDays <= 7)
                .Select(g => g.First()))
            .Select(b => b.Path)
            .ToHashSet();
        foreach (var b in all.Where(b => !keep.Contains(b.Path)))
            try { File.Delete(b.Path); } catch { } // best-effort prune; LogService not alive during Load
    }

    public void ExportProfile(LayoutProfile profile, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));

    public LayoutProfile ImportProfile(string path)
    {
        var profile = JsonSerializer.Deserialize<LayoutProfile>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("Profile file could not be read.");
        profile.Id = Guid.NewGuid().ToString("N");
        return profile;
    }

    private static void EnsureDefaults(AppSettings settings)
    {
        if (settings.Assignments.Count == 0)
        {
            var labels = new[] { "Main", "Alt 1", "Alt 2", "Alt 3", "Alt 4", "Hauler", "Scout", "Logi", "DPS" };
            for (var i = 0; i < 8; i++)
            {
                settings.Assignments.Add(new SlotAssignment { SlotNumber = i + 1, Label = labels[i] });
            }
        }

        // Migrate single-window assignments to multi-window list (first-run after update).
        foreach (var assignment in settings.Assignments)
        {
            if (!string.IsNullOrWhiteSpace(assignment.AssignedWindowTitle) && assignment.AssignedWindows.Count == 0)
            {
                assignment.AssignedWindows.Add(new SlotWindowEntry
                {
                    Title = assignment.AssignedWindowTitle,
                    LastProcessId = assignment.LastProcessId,
                    LastHandleHex = assignment.LastHandleHex
                });
                assignment.AssignedWindowTitle = null;
                assignment.LastProcessId = null;
                assignment.LastHandleHex = null;
            }
        }

        if (settings.Profiles.Count == 0)
        {
            settings.Profiles = new ObservableCollection<LayoutProfile>(PresetFactory.CreateBuiltInProfiles());
            settings.ActiveProfileId = settings.Profiles.First().Id;
        }
        else
        {
            PresetFactory.EnsureBuiltInProfiles(settings.Profiles);
        }

        // Ensure custom profiles have a category set (old saves predate the Category field).
        foreach (var profile in settings.Profiles)
        {
            if (string.IsNullOrEmpty(profile.Category))
                profile.Category = profile.IsBuiltIn ? "Stacked" : "Custom";
        }

        if (string.IsNullOrWhiteSpace(settings.ActiveProfileId) || settings.Profiles.All(p => p.Id != settings.ActiveProfileId))
        {
            settings.ActiveProfileId = settings.Profiles.FirstOrDefault()?.Id ?? "";
        }

        if (settings.Hotkeys.Count == 0)
        {
            settings.Hotkeys = new ObservableCollection<HotkeyBinding>(HotkeyDefaults.Create());
        }
        else
        {
            var existingActions = settings.Hotkeys.Select(h => h.ActionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in HotkeyDefaults.Create())
            {
                if (!existingActions.Contains(binding.ActionId))
                    settings.Hotkeys.Add(binding);
            }

            BackfillSwapMasterGestures(settings.Hotkeys);
            PruneRetiredHotkeys(settings.Hotkeys);
        }

        ReorderCharacterHotkeysFirst(settings.Hotkeys);

        // Migrate to CharacterSets: wrap the existing Assignments+Hotkeys into the Default set.
        if (settings.CharacterSets.Count == 0)
        {
            var defaultSet = new Models.CharacterSet { Name = "Default" };
            foreach (var a in settings.Assignments) defaultSet.Assignments.Add(a);
            foreach (var h in settings.Hotkeys) defaultSet.Hotkeys.Add(h);
            settings.CharacterSets.Add(defaultSet);
            settings.ActiveCharacterSetId = defaultSet.Id;
        }
        else if (string.IsNullOrEmpty(settings.ActiveCharacterSetId)
            || settings.CharacterSets.All(s => s.Id != settings.ActiveCharacterSetId))
        {
            settings.ActiveCharacterSetId = settings.CharacterSets[0].Id;
        }

        // Mark the active set so the UI toggle buttons show it as selected.
        foreach (var s in settings.CharacterSets)
            s.IsActive = s.Id == settings.ActiveCharacterSetId;
    }

    // The "Switch to character" hotkeys are the most-used rows, so surface them at the top of the
    // Hotkeys grid (which renders in collection order) instead of buried below the focus/swap rows.
    // OrderBy is a stable sort, so the relative order within each group is preserved.
    private static void ReorderCharacterHotkeysFirst(ObservableCollection<HotkeyBinding> hotkeys)
    {
        var ordered = hotkeys
            .OrderBy(h => h.ActionId.StartsWith("SwitchToCharacter", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = hotkeys.IndexOf(ordered[i]);
            if (current != i) hotkeys.Move(current, i);
        }
    }

    // Remove hotkey rows for actions no longer offered (e.g. FocusSlot6-15, Cycle, Borderless/Restore
    // style) so the Hotkeys tab stays lean. The current HotkeyDefaults set is the canonical list;
    // anything not in it is retired and dropped, even if previously bound.
    private static void PruneRetiredHotkeys(ObservableCollection<HotkeyBinding> hotkeys)
    {
        var keep = HotkeyDefaults.Create().Select(h => h.ActionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retired = hotkeys.Where(h => !keep.Contains(h.ActionId)).ToList();
        foreach (var h in retired) hotkeys.Remove(h);
    }

    // The corner→master swap actions originally shipped unbound (VirtualKey 0). For saves created
    // before defaults were assigned, fill in the default gesture when the action is still unbound and
    // the gesture isn't already taken. User-chosen or deliberately cleared bindings are left untouched
    // (a deliberate clear that lands on the default gesture is indistinguishable, an acceptable trade
    // for making the core mechanic work out of the box).
    private static void BackfillSwapMasterGestures(ObservableCollection<HotkeyBinding> hotkeys)
    {
        var swapActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SwapFocusedWithMaster", "SwapSlotWithMaster1", "SwapSlotWithMaster2",
            "SwapSlotWithMaster3", "SwapSlotWithMaster4",
        };

        foreach (var def in HotkeyDefaults.Create())
        {
            if (!swapActions.Contains(def.ActionId) || def.VirtualKey == 0) continue;

            var existing = hotkeys.FirstOrDefault(h => h.ActionId.Equals(def.ActionId, StringComparison.OrdinalIgnoreCase));
            if (existing is null || existing.VirtualKey != 0) continue; // user already bound it

            var taken = hotkeys.Any(h => h.VirtualKey == def.VirtualKey && h.Modifiers == def.Modifiers && h.VirtualKey != 0);
            if (taken) continue;

            existing.Modifiers = def.Modifiers;
            existing.VirtualKey = def.VirtualKey;
            existing.GestureText = def.GestureText;
        }
    }
}
