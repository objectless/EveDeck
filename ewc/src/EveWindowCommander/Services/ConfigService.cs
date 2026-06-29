using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using EveWindowCommander.Models;

namespace EveWindowCommander.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string AppDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EVE Window Commander");

    public string ConfigPath => Path.Combine(AppDataFolder, "settings.json");
    public string LogsFolder => Path.Combine(AppDataFolder, "logs");
    public string BackupsFolder => Path.Combine(AppDataFolder, "backups");

    public AppSettings Load()
    {
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
                try { File.Move(ConfigPath, bakPath, overwrite: true); } catch { }
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
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    // ── Backup ────────────────────────────────────────────────────────────────

    // Copy current settings to a timestamped file in BackupsFolder, keeping at most `keep` files.
    public void CreateBackup(int keep = 10)
    {
        if (!File.Exists(ConfigPath)) return;
        Directory.CreateDirectory(BackupsFolder);
        var stamp = DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss");
        var dest = Path.Combine(BackupsFolder, $"settings_backup_{stamp}.json");
        // Avoid duplicate stamps if called twice in the same second.
        if (!File.Exists(dest))
            try { File.Copy(ConfigPath, dest); } catch { }
        PruneBackups(keep);
    }

    public IReadOnlyList<SettingsBackup> GetBackups()
    {
        if (!Directory.Exists(BackupsFolder)) return Array.Empty<SettingsBackup>();
        return Directory.GetFiles(BackupsFolder, "settings_backup_*.json")
            .Select(p => new SettingsBackup(File.GetLastWriteTime(p), p))
            .OrderByDescending(b => b.Timestamp)
            .ToList();
    }

    // Overwrite settings.json with a backup file. Caller is responsible for restarting the app.
    public void RestoreBackup(string backupPath)
    {
        File.Copy(backupPath, ConfigPath, overwrite: true);
    }

    private void PruneBackups(int keep)
    {
        var old = GetBackups().Skip(keep).ToList();
        foreach (var b in old)
            try { File.Delete(b.Path); } catch { }
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
