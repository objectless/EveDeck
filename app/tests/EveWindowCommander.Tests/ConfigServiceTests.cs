using Xunit;
using EveWindowCommander.Services;
using EveWindowCommander.Models;
using System.IO;
using System.Text.Json;

namespace EveWindowCommander.Tests;

public class ConfigServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConfigService _configService;

    public ConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configService = new ConfigService(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void Load_NoFile_CreatesDefaultsAndPersists()
    {
        var settings = _configService.Load();

        Assert.Equal(8, settings.Assignments.Count);
        Assert.NotEmpty(settings.Profiles);

        var gridProfile = settings.Profiles.FirstOrDefault(p => p.Category == "Grid");
        Assert.NotNull(gridProfile);

        var centerMasterProfile = settings.Profiles.FirstOrDefault(p => p.Category == "Center Master");
        Assert.NotNull(centerMasterProfile);

        Assert.NotEmpty(settings.ActiveProfileId);
        Assert.Contains(settings.Profiles, p => p.Id == settings.ActiveProfileId);

        Assert.True(File.Exists(_configService.ConfigPath));
    }

    [Fact]
    public void Load_CorruptJson_BacksUpBakAndStartsFresh()
    {
        Directory.CreateDirectory(_tempDir);
        var corruptContent = "{ this is not valid json }";
        File.WriteAllText(_configService.ConfigPath, corruptContent);

        var settings = _configService.Load();
        Assert.Equal(8, settings.Assignments.Count);

        Assert.True(File.Exists(_configService.ConfigPath + ".bak"));
        Assert.Equal(corruptContent, File.ReadAllText(_configService.ConfigPath + ".bak"));
    }

    [Fact]
    public void Load_MigratesSingleWindowAssignmentToList()
    {
        var settings = _configService.Load();

        var firstAssignment = settings.Assignments[0];
        firstAssignment.AssignedWindowTitle = "Test Window";
        firstAssignment.LastProcessId = 1234;
        firstAssignment.LastHandleHex = "deadbeef";

        _configService.Save(settings);

        var configService2 = new ConfigService(_tempDir);
        var reloadedSettings = configService2.Load();

        var reloadedAssignment = reloadedSettings.Assignments[0];
        Assert.NotNull(reloadedAssignment.AssignedWindows);
        Assert.NotEmpty(reloadedAssignment.AssignedWindows);

        var windowEntry = reloadedAssignment.AssignedWindows.First();
        Assert.Equal("Test Window", windowEntry.Title);
        Assert.Null(reloadedAssignment.AssignedWindowTitle);
    }

    [Fact]
    public void Load_WrapsLegacySettingsIntoDefaultCharacterSet()
    {
        var settings = _configService.Load();

        Assert.NotEmpty(settings.CharacterSets);
        var defaultSet = settings.CharacterSets.FirstOrDefault(cs => cs.Name == "Default");
        Assert.NotNull(defaultSet);

        Assert.NotEmpty(defaultSet.Assignments);
        Assert.NotEmpty(defaultSet.Hotkeys);
    }

    [Fact]
    public void CreateBackup_SkipsWhenSettingsCorrupt()
    {
        _configService.Load();

        var backupsBefore = Directory.Exists(_configService.BackupsFolder)
            ? Directory.GetFiles(_configService.BackupsFolder, "settings_backup_*.json").Length
            : 0;

        File.WriteAllText(_configService.ConfigPath, "corrupt json");

        _configService.CreateBackup();

        var backupsAfter = Directory.Exists(_configService.BackupsFolder)
            ? Directory.GetFiles(_configService.BackupsFolder, "settings_backup_*.json").Length
            : 0;

        Assert.Equal(backupsBefore, backupsAfter);
    }

    [Fact]
    public void RestoreBackup_ThrowsOnCorruptBackup()
    {
        var corruptBackupPath = Path.Combine(_tempDir, "corrupt_backup.json");
        File.WriteAllText(corruptBackupPath, "not valid json");

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            _configService.RestoreBackup(corruptBackupPath);
        });

        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsProfiles()
    {
        var settings = _configService.Load();

        var customProfile = new LayoutProfile
        {
            Name = "Custom Test Profile",
            Category = "Custom"
        };
        customProfile.Slots.Add(new LayoutSlot { SlotNumber = 1, X = 0, Y = 0, Width = 100, Height = 100 });

        settings.Profiles.Add(customProfile);
        _configService.Save(settings);

        var configService2 = new ConfigService(_tempDir);
        var reloadedSettings = configService2.Load();

        var foundProfile = reloadedSettings.Profiles.FirstOrDefault(p => p.Name == "Custom Test Profile");
        Assert.NotNull(foundProfile);
        Assert.Single(foundProfile.Slots);
        Assert.Equal(1, foundProfile.Slots[0].SlotNumber);
    }
}
