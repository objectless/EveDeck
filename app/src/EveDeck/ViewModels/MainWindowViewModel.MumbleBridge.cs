using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using EveDeck.Services;
using EveDeck.Utilities;
using EveDeck.Views;

namespace EveDeck.ViewModels;

// One row in the EveDeck-rendered talker overlay. Rows are rebuilt wholesale on every bridge
// event (rosters are small), so this can stay an immutable record.
public sealed record TalkerRow(string Name, int State)
{
    public bool IsTalking => State is 1 or 2 or 3; // talking / whispering / shouting
}

// Native Mumble integration: EveDeck hosts a named-pipe server (MumbleBridgeService) that a tiny
// plugin inside the user's own Mumble client feeds with talking-state events, and renders its own
// themed talker panel (TalkerOverlayWindow). This replaces fighting Mumble's Qt "Talking UI"
// window for users who install the plugin; the window-pinning overlay remains as the
// zero-install fallback.
public sealed partial class MainWindowViewModel
{
    private MumbleBridgeService? _mumbleBridge;
    private TalkerOverlayWindow? _talkerWindow;

    // Re-filters the "recently active" rows so users fade out a few seconds after they stop
    // talking even when no new pipe events arrive.
    private readonly DispatcherTimer _talkerPruneTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static readonly TimeSpan RecentlyActiveWindow = TimeSpan.FromSeconds(8);

    public ObservableCollection<TalkerRow> TalkerRows { get; } = new();

    private string _talkerChannelName = "Mumble";
    public string TalkerChannelName
    {
        get => _talkerChannelName;
        private set => SetProperty(ref _talkerChannelName, value);
    }

    public RelayCommand InstallMumblePluginCommand { get; private set; } = null!;

    private string _mumblePluginInstallStatus = "";
    public string MumblePluginInstallStatus
    {
        get => _mumblePluginInstallStatus;
        private set => SetProperty(ref _mumblePluginInstallStatus, value);
    }

    public bool TalkerOverlayEnabled
    {
        get => _settings.TalkerOverlay.Enabled;
        set
        {
            if (_settings.TalkerOverlay.Enabled == value) return;
            _settings.TalkerOverlay.Enabled = value;
            OnPropertyChanged();
            if (value) StartTalkerOverlay();
            else StopTalkerOverlay();
            OnPropertyChanged(nameof(TalkerOverlayStatus));
            Save();
        }
    }

    public int TalkerOverlayOpacity
    {
        get => _settings.TalkerOverlay.OpacityPercent;
        set
        {
            var clamped = Math.Clamp(value, 20, 100);
            if (_settings.TalkerOverlay.OpacityPercent == clamped) return;
            _settings.TalkerOverlay.OpacityPercent = clamped;
            OnPropertyChanged();
            _talkerWindow?.ApplyOpacity();
            Save();
        }
    }

    public string TalkerOverlayStatus => !_settings.TalkerOverlay.Enabled
        ? "Disabled"
        : _mumbleBridge?.PluginConnected == true
            ? $"Connected — {(_mumbleBridge.ChannelName.Length > 0 ? _mumbleBridge.ChannelName : "no channel")}"
            : "Waiting for the Mumble plugin — install it below and enable it in Mumble's Configure > Plugins.";

    partial void InitMumbleBridge()
    {
        InstallMumblePluginCommand = new RelayCommand(InstallMumblePlugin);
        _talkerPruneTimer.Tick += (_, _) => RefreshTalkerRows();
        if (_settings.TalkerOverlay.Enabled) StartTalkerOverlay();
    }

    private void StartTalkerOverlay()
    {
        if (_mumbleBridge is null)
        {
            // LogService.Entries is an ObservableCollection bound to the Logs tab's CollectionView,
            // which throws if mutated off the UI thread -- OnLog fires from the pipe's background
            // listener thread, so it must be marshaled (this was the actual reason the bridge
            // looked like it never connected: the first log call after a connection crashed the
            // listen loop with an unhandled NotSupportedException).
            _mumbleBridge = new MumbleBridgeService
            {
                OnLog = msg => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Log.Info(msg)),
            };
            _mumbleBridge.Changed += OnMumbleBridgeChanged;
            _mumbleBridge.Start();
        }
        if (_talkerWindow is null)
        {
            _talkerWindow = new TalkerOverlayWindow { DataContext = this };
            _talkerWindow.Bind(_settings.TalkerOverlay, Save);
        }
        _talkerWindow.Show();
        _talkerPruneTimer.Start();
        Log.Info("Talker overlay started (waiting for Mumble plugin events).");
    }

    private void StopTalkerOverlay()
    {
        _talkerPruneTimer.Stop();
        if (_mumbleBridge is not null)
        {
            _mumbleBridge.Changed -= OnMumbleBridgeChanged;
            _mumbleBridge.Dispose();
            _mumbleBridge = null;
        }
        _talkerWindow?.Close();
        _talkerWindow = null;
        TalkerRows.Clear();
    }

    private void OnMumbleBridgeChanged()
        => System.Windows.Application.Current.Dispatcher.BeginInvoke(RefreshTalkerRows);

    // Rebuilds the visible rows: everyone currently talking, plus anyone who stopped within the
    // recently-active window (mirrors Mumble's own "talking and recently active" overlay filter,
    // so a big fleet channel doesn't turn the panel into a 50-name wall).
    private void RefreshTalkerRows()
    {
        if (_mumbleBridge is null) return;

        var now = DateTime.UtcNow;
        var rows = _mumbleBridge.GetSnapshot()
            .Where(t => t.State > 0 || now - t.LastActiveUtc < RecentlyActiveWindow)
            .OrderByDescending(t => t.State > 0)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TalkerRow(t.Name, t.State))
            .ToList();

        if (!rows.SequenceEqual(TalkerRows))
        {
            TalkerRows.Clear();
            foreach (var row in rows) TalkerRows.Add(row);
        }

        TalkerChannelName = _mumbleBridge.PluginConnected && _mumbleBridge.ChannelName.Length > 0
            ? _mumbleBridge.ChannelName
            : "Mumble";
        OnPropertyChanged(nameof(TalkerOverlayStatus));
    }

    // Copies the bundled plugin DLL into Mumble's user plugin folder. The user still has to
    // enable it inside Mumble (Configure > Plugins) -- EveDeck deliberately never touches
    // Mumble's own configuration.
    private void InstallMumblePlugin()
    {
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "MumblePlugin", "EveDeckMumblePlugin.dll");
            if (!File.Exists(source))
            {
                MumblePluginInstallStatus = "Plugin DLL not found next to EveDeck.exe — reinstall EveDeck.";
                return;
            }
            // Mumble's user plugin dir nests the Qt org AND app name: %APPDATA%\Mumble\Mumble\Plugins.
            // The shorter %APPDATA%\Mumble\Plugins also exists on disk but is never scanned
            // (verified empirically against Mumble 1.5 -- the DLL sat there invisible to Mumble).
            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mumble", "Mumble", "Plugins");
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, "EveDeckMumblePlugin.dll");
            try
            {
                File.Copy(source, dest, overwrite: true);
            }
            catch (IOException)
            {
                // Mumble is running and holds the old DLL open. Windows still allows RENAMING a
                // loaded DLL, so shunt it aside and copy the new one in; Mumble picks the new
                // file up on its next restart. The .old file is cleaned up on later installs.
                var old = dest + ".old";
                File.Delete(old);
                File.Move(dest, old);
                File.Copy(source, dest);
            }
            MumblePluginInstallStatus =
                "Installed. In Mumble: Configure > Plugins > enable \"EveDeck Talker Bridge\" (restart Mumble if it isn't listed).";
            Log.Info($"Mumble plugin copied to {destDir}.");
        }
        catch (Exception ex)
        {
            MumblePluginInstallStatus = $"Install failed: {ex.Message}";
            Log.Warn($"Mumble plugin install failed: {ex.Message}");
        }
    }
}
