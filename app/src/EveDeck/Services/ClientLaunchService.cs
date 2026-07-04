using System.Diagnostics;
using System.IO;
using EveDeck.Models;

namespace EveDeck.Services;

// Launches one EVE Launcher process per named character in a CharacterSet ("launch group"),
// spaced out by CharacterSet.LaunchDelayMs so Windows/network isn't hammered. Ordinary process
// spawning only — no credential handling or UI automation of the launcher (COMPLIANCE.md).
public sealed class ClientLaunchService
{
    private static readonly string[] CommonLauncherPaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Online", "eveLauncher.exe"),
        @"C:\Program Files (x86)\Steam\steamapps\common\EVE Online\eveLauncher.exe"
    };

    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    public string? FindLauncherPath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        foreach (var candidate in CommonLauncherPaths)
            if (File.Exists(candidate))
                return candidate;

        return null;
    }

    public async Task LaunchGroupAsync(CharacterSet group, string? launcherPathOverride, CancellationToken ct)
    {
        var launcherPath = FindLauncherPath(launcherPathOverride);
        if (launcherPath is null)
        {
            ErrorOccurred?.Invoke("Could not find the EVE Launcher. Set a custom path in Options.");
            return;
        }

        var targets = group.Assignments.Where(a => !string.IsNullOrWhiteSpace(a.Label)).ToList();
        if (targets.Count == 0)
        {
            ErrorOccurred?.Invoke($"'{group.Name}' has no named seats to launch.");
            return;
        }

        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var seat = targets[i];
            try
            {
                Process.Start(new ProcessStartInfo(launcherPath) { UseShellExecute = true });
                StatusChanged?.Invoke($"Launched EVE Launcher for '{seat.Label}' ({i + 1}/{targets.Count}).");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Failed to launch EVE Launcher for '{seat.Label}': {ex.Message}");
            }

            if (i < targets.Count - 1)
                await Task.Delay(group.LaunchDelayMs, ct);
        }

        StatusChanged?.Invoke($"Finished launching group '{group.Name}' ({targets.Count} client(s)).");
    }
}
