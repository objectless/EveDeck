using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Velopack;
using Velopack.Sources;

namespace EveDeck.Services;

public enum InstallKind { Velopack, Inno, Unknown }

/// <summary>
/// Detects how the running copy of EveDeck got onto the machine and applies an update using the
/// matching mechanism: Velopack's own update manager for Velopack-managed installs (portable or
/// Velopack-installed), or a silent re-run of the Inno Setup installer for Inno-managed installs.
/// Anything else (a raw zip extracted before this feature existed) has no mechanism and is the
/// caller's job to fall back to a manual download link.
/// </summary>
public sealed class UpdateApplyService
{
    private const string GithubRepoUrl = "https://github.com/objectless/EveDeck";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly LogService? _log;

    public UpdateApplyService(LogService? log = null)
    {
        _log = log;
    }

    public InstallKind DetectInstallKind()
    {
        var velopackManager = TryCreateVelopackManager();
        if (velopackManager is { IsInstalled: true })
            return InstallKind.Velopack;

        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "unins000.exe")))
            return InstallKind.Inno;

        return InstallKind.Unknown;
    }

    /// <summary>
    /// Downloads the given Setup.exe and re-runs it silently over the existing (fixed-AppId,
    /// per-user) install, then closes this process so the installer isn't blocked by a file lock
    /// on the running exe -- /CLOSEAPPLICATIONS would handle that too, but this codebase has a
    /// known sharp edge around "file locked by running EveDeck", so don't leave it to chance.
    /// Relaunch after install is handled by the installer's own [Run] section (EveDeck.iss), not
    /// /RESTARTAPPLICATIONS here -- confirmed via local testing that /RESTARTAPPLICATIONS alone
    /// does not reliably relaunch a /VERYSILENT install, and adding it back would risk a double
    /// launch racing our own single-instance mutex into showing an unwanted "already running" popup.
    /// </summary>
    public async Task ApplyInnoUpdateAsync(string installerUrl)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"EveDeck-Setup-{Guid.NewGuid():N}.exe");
        var bytes = await Http.GetByteArrayAsync(installerUrl);
        await File.WriteAllBytesAsync(tempPath, bytes);

        Process.Start(new ProcessStartInfo(tempPath)
        {
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true
        });

        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Checks, downloads, and applies via Velopack, restarting the app when done.</summary>
    public async Task ApplyVelopackUpdateAsync()
    {
        var mgr = TryCreateVelopackManager();
        if (mgr is not { IsInstalled: true }) return;

        var info = await mgr.CheckForUpdatesAsync();
        if (info is null) return;

        await mgr.DownloadUpdatesAsync(info);
        mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    private UpdateManager? TryCreateVelopackManager()
    {
        try
        {
            return new UpdateManager(new GithubSource(GithubRepoUrl, null, false));
        }
        catch (Exception ex)
        {
            _log?.Warn($"Velopack UpdateManager unavailable: {ex.Message}");
            return null;
        }
    }
}
