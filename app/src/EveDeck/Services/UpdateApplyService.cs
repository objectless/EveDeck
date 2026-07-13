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
    /// Downloads the given Setup.exe (reporting progress via <paramref name="onProgress"/>) and
    /// re-runs it over the existing (fixed-AppId, per-user) install using /SILENT rather than
    /// /VERYSILENT -- both are equally unattended (no wizard pages, no "close applications"
    /// prompt), but /SILENT keeps Inno's small install-progress window visible so the update
    /// stays noticeable through the file-copy phase instead of the app just vanishing. Closes
    /// this process right after launching the installer so it isn't blocked by a file lock on the
    /// running exe -- /CLOSEAPPLICATIONS would handle that too, but this codebase has a known
    /// sharp edge around "file locked by running EveDeck", so don't leave it to chance.
    /// Relaunch after install is handled by the installer's own [Run] section (EveDeck.iss), not
    /// /RESTARTAPPLICATIONS here -- confirmed via local testing that /RESTARTAPPLICATIONS alone
    /// does not reliably relaunch a silent install, and adding it back would risk a double
    /// launch racing our own single-instance mutex into showing an unwanted "already running" popup.
    /// </summary>
    public async Task ApplyInnoUpdateAsync(string installerUrl, Action<string, double?>? onProgress = null)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"EveDeck-Setup-{Guid.NewGuid():N}.exe");

        onProgress?.Invoke("Downloading update...", null);
        using (var response = await Http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using var httpStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(tempPath);
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;
                if (totalBytes is > 0)
                    onProgress?.Invoke("Downloading update...", 100.0 * totalRead / totalBytes.Value);
            }
        }

        onProgress?.Invoke("Installing update...", null);

        Process.Start(new ProcessStartInfo(tempPath)
        {
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
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
