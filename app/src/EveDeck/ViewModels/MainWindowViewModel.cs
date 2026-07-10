using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;
using EveDeck.Views;
using Microsoft.Win32;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ConfigService _configService = new();
    private readonly Win32WindowService _windowService = new();
    private readonly AppSettings _settings;
    private readonly ClientLaunchService _clientLaunchService = new();
    private readonly ChatLogWatcherService _chatLogWatcherService = new();
    private readonly GameLogWatcherService _gameLogWatcherService = new();
    private CancellationTokenSource? _launchGroupCts;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _hoverPeekTimer = new() { IsEnabled = false };
    // Debounce auto-apply so all clients have time to launch and settle before we move them.
    // Must stay LONGER than _refreshTimer's interval (5s): new-client detection only runs once per
    // refresh poll, so a debounce shorter than the poll interval can fire before the next poll has
    // a chance to see a straggler that logged in a couple of seconds late -- exactly what happens
    // during a mass multi-account login, where the auto-apply then only parks/sizes whichever
    // subset had appeared by that premature pass, leaving later clients unparked until a manual
    // reapply catches everyone in one clean pass.
    private readonly DispatcherTimer _autoApplyTimer = new() { Interval = TimeSpan.FromSeconds(7) };

    private readonly Dictionary<int, nint> _lastFocusedHandle = new();

    // Titles of assigned EVE windows seen at the previous refresh, used to detect newly-launched
    // clients so the active profile can be auto-applied.
    private readonly HashSet<string> _knownAssignedTitles = new(StringComparer.OrdinalIgnoreCase);
    private bool _clientBaselineInitialized;

    // 1a — Session-level style snapshots keyed by HWND (not persisted, resets on restart).
    private readonly Dictionary<nint, StyleSnapshot> _sessionSnapshots = new();

    // 1b — Last known frame handle/rect to skip redundant overlay repositions.
    private nint _lastFrameHandle;
    private WindowRect? _lastFrameRect;

    // 1c — Guard against re-entrant profile apply.
    private bool _applyInProgress;

    // 2d — Profile search filter text.
    private string _profileSearchText = "";

    // 2h — Log level filter.
    private string _logFilterLevel = "All";

    // 3b — Window rects captured just before the last profile apply (for undo).
    private Dictionary<string, WindowRect>? _undoRects;

    private UpdateCheckService.UpdateInfo? _availableUpdate;
    private bool _updateBannerDismissed;
    private bool _isCheckingForUpdate;
    private bool _configResetBannerDismissed;
    private IReadOnlyList<string> _hotkeyConflicts = Array.Empty<string>();
    private bool _hotkeyConflictBannerDismissed;
    private ActiveFrameOverlay? _frameOverlay;
    private Brush _frameBrush = Brushes.Orange;
    private EveWindowInfo? _selectedWindow;
    private SlotAssignment? _selectedAssignment;
    private LayoutProfile? _selectedProfile;
    private HotkeyBinding? _selectedHotkey;
    private HotkeyBinding? _capturingHotkey;
    private string _status = "Ready.";
    private string _lastUpdatedText = "Not refreshed yet";

    public MainWindowViewModel()
    {
        _settings = _configService.Load();
        Log = new LogService(_configService.LogsFolder);
        // Surface overlay diagnostics (failed thumbnail registrations etc.) in the Logs tab.
        Views.TileSurfaceWindow.Log = msg => Log.Warn(msg);
        Assignments = _settings.Assignments;
        Profiles = _settings.Profiles;
        Hotkeys = _settings.Hotkeys;
        Logs = Log.Entries;

        // Migrate the former global master seat onto each profile (masters are now per-profile). Profiles
        // that have never stored one inherit the old global value; once a profile records its own (>0) it
        // keeps that choice across launches.
        foreach (var p in Profiles)
            if (p.MasterSeat == 0) p.MasterSeat = _settings.MasterSlotNumber;

        // ── Commands ──────────────────────────────────────────────
        RefreshCommand = new RelayCommand(Refresh);
        AssignSelectedCommand = new RelayCommand(AssignSelected, () => SelectedWindow is not null && SelectedAssignment is not null);
        AssignWindowToSlotCommand = new RelayCommand(AssignWindowToSlot, _ => SelectedWindow is not null);
        RemoveWindowFromSlotCommand = new RelayCommand(RemoveWindowFromSlot);
        ClearAssignmentCommand = new RelayCommand(() =>
        {
            if (SelectedAssignment is null) return;
            ClearAssignment(SelectedAssignment);
        });
        ClearSlotCommand = new RelayCommand(parameter =>
        {
            if (parameter is SlotAssignment assignment) ClearAssignment(assignment);
        });
        FocusSlotCommand = new RelayCommand(parameter =>
        {
            if (parameter is SlotAssignment assignment) FocusSlot(assignment.SlotNumber);
        });
        AddSlotCommand = new RelayCommand(AddSlot);
        DeleteSelectedSlotCommand = new RelayCommand(DeleteSelectedSlot, () => SelectedAssignment is not null && Assignments.Count > 1);
        AutoAssignAllCommand = new RelayCommand(AutoAssignAll);  // 2e
        ApplyProfileCommand = new RelayCommand(() => ApplyActiveProfile());
        CaptureProfileCommand = new RelayCommand(CaptureAssignedWindows);
        UndoLastApplyCommand = new RelayCommand(UndoLastApply, () => _undoRects is not null && !_applyInProgress);  // 3b
        SaveCommand = new RelayCommand(Save);
        RestoreSelectedStyleCommand = new RelayCommand(RestoreSelectedStyle, () => SelectedWindow is not null);
        ToggleSelectedBorderlessCommand = new RelayCommand(ToggleSelectedBorderless, () => SelectedWindow is not null);
        NewProfileCommand = new RelayCommand(NewProfile);
        EditLayoutCommand = new RelayCommand(EditLayoutOnMonitor);
        DuplicateProfileCommand = new RelayCommand(DuplicateProfile, () => SelectedProfile is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null && !SelectedProfile.IsBuiltIn && Profiles.Count > 1);
        ImportProfileCommand = new RelayCommand(ImportProfile);
        ExportProfileCommand = new RelayCommand(ExportProfile, () => SelectedProfile is not null);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        CaptureHotkeyCommand = new RelayCommand(BeginHotkeyCapture, parameter => parameter is HotkeyBinding || SelectedHotkey is not null);
        ClearHotkeyCommand = new RelayCommand(ClearHotkey, parameter => parameter is HotkeyBinding || SelectedHotkey is not null);
        ResetHotkeysCommand = new RelayCommand(ResetHotkeysToDefaults);  // 2c
        SetMasterSlotCommand = new RelayCommand(SetMasterSlot);
        AddEsiCharacterCommand = new RelayCommand(AddEsiCharacter);
        RemoveEsiCharacterCommand = new RelayCommand(RemoveEsiCharacter);
        RestoreBackupCommand = new RelayCommand(() => RestoreSelectedBackup(), () => SelectedBackup is not null);
        DismissUpdateBannerCommand = new RelayCommand(() => { _updateBannerDismissed = true; OnPropertyChanged(nameof(ShowUpdateBanner)); });
        DismissConfigResetBannerCommand = new RelayCommand(() => { _configResetBannerDismissed = true; OnPropertyChanged(nameof(ShowConfigResetBanner)); });
        DismissHotkeyConflictBannerCommand = new RelayCommand(() => { _hotkeyConflictBannerDismissed = true; OnPropertyChanged(nameof(ShowHotkeyConflictBanner)); });
        InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync());
        CheckForUpdateCommand = new RelayCommand(() => _ = CheckForUpdateAsync(manual: true));
        SpawnTestWindowsCommand = new RelayCommand(SpawnTestWindows);
        AutoSelectBestProfileCommand = new RelayCommand(AutoSelectBestProfile);
        SetMasterResolutionCommand = new RelayCommand(ExecuteSetMasterResolution, () => SelectedMasterResolution is not null && SelectedProfile is not null);
        ClearMasterResolutionCommand = new RelayCommand(ExecuteClearMasterResolution, () => SelectedProfile is not null && SelectedProfile.MasterResolutionWidth > 0);
        ToggleTopmostCommand = new RelayCommand(parameter =>
        {
            if (parameter is not SlotAssignment seat) return;
            // IsTopmost is already updated by the TwoWay binding before this command fires.
            if (seat.IsTopmost)
            {
                // Pinned: raise now only if EVE is currently focused; the foreground hook keeps it in sync.
                var eveForeground = IsEveWindowForeground();
                foreach (var w in FindAssignedWindows(seat))
                    _windowService.SetWindowTopmost(w.Handle, eveForeground);
            }
            else
            {
                // Un-pinned: drop to normal z-order immediately, regardless of what's focused.
                foreach (var w in FindAssignedWindows(seat))
                    _windowService.SetWindowTopmost(w.Handle, false);
            }
            _lastEveForeground = null; // pinned set changed; re-evaluate on the next foreground change
            Save();
            Log.Info($"Seat {seat.SlotNumber} ({seat.Label}) above-while-EVE-focused: {seat.IsTopmost}.");
        });

        SwitchCharacterSetCommand = new RelayCommand(parameter =>
        {
            if (parameter is Models.CharacterSet set)
                SwitchToCharacterSet(set.Id);
        });

        AddCharacterSetCommand = new RelayCommand(_ => AddCharacterSet());

        DeleteCharacterSetCommand = new RelayCommand(parameter =>
        {
            var set = parameter as Models.CharacterSet ?? ActiveCharacterSet;
            if (set is not null) DeleteCharacterSet(set);
        }, _ => _settings.CharacterSets.Count > 1);

        LaunchGroupCommand = new RelayCommand(LaunchGroup, parameter => parameter is Models.CharacterSet);
        AddChatAlertRuleCommand = new RelayCommand(AddChatAlertRule);
        RemoveChatAlertRuleCommand = new RelayCommand(RemoveChatAlertRule, parameter => parameter is ChatAlertRule);
        AddGameEventRuleCommand = new RelayCommand(AddGameEventRule);
        RemoveGameEventRuleCommand = new RelayCommand(RemoveGameEventRule, parameter => parameter is GameEventRule);
        AddOverlayAllowedAppCommand = new RelayCommand(AddOverlayAllowedApp);
        RemoveOverlayAllowedAppCommand = new RelayCommand(RemoveOverlayAllowedApp, parameter => parameter is Models.OverlayAllowedApp);

        // ── Views ─────────────────────────────────────────────────
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId) ?? Profiles.FirstOrDefault();

        ProfilesView = CollectionViewSource.GetDefaultView(Profiles);
        ProfilesView.SortDescriptions.Add(new SortDescription(nameof(LayoutProfile.GroupOrder), ListSortDirection.Ascending));
        ProfilesView.SortDescriptions.Add(new SortDescription(nameof(LayoutProfile.Name), ListSortDirection.Ascending));
        ProfilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LayoutProfile.Category)));

        WindowsView = new CollectionViewSource { Source = Windows }.View;
        WindowsView.Filter = o => o is EveWindowInfo w && !IsWindowAssigned(w);

        LogsView = CollectionViewSource.GetDefaultView(Logs);  // 2h

        // ── Timers ────────────────────────────────────────────────
        _refreshTimer.Tick += (_, _) => Refresh();
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); Save(); };
        _autoApplyTimer.Tick += (_, _) =>
        {
            _autoApplyTimer.Stop();
            if (_applyInProgress) return;
            Log.Info("Detected newly-launched EVE client(s); re-applying active profile automatically.");
            ApplyActiveProfile();
        };
        if (_settings.AutoRefresh) _refreshTimer.Start();

        SyncMasterSlot();
        UpdatePositionCodes();
        RaiseIdentityDependents();
        SubscribeToAssignmentChanges();
        SubscribeToHotkeyChanges();

        _frameBrush = ParseFrameBrush(_settings.ActiveFrameColor);
        _frameTimer.Tick += OnFrameTick;
        _hoverPeekTimer.Tick += OnHoverPeekTimerTick;
        if (_settings.ActiveFrameEnabled) StartFrameOverlay();

        Refresh();
        LoadMasterResolutions();

        // 2g — Apply startup profile if configured and windows are detected.
        if (_settings.ApplyProfileOnStartup && !string.IsNullOrWhiteSpace(_settings.StartupProfileId) && Windows.Count > 0)
        {
            var startupProfile = Profiles.FirstOrDefault(p => p.Id == _settings.StartupProfileId);
            if (startupProfile is not null)
            {
                SelectedProfile = startupProfile;
                ApplyActiveProfile();
                Log.Info($"Applied startup profile: {startupProfile.Name}.");

                // The Refresh() above already armed the auto-apply debounce timer, since it saw
                // these already-running clients as "newly appeared" on the first scan. We just
                // applied for them directly above, so cancel the pending re-apply to avoid doing
                // it twice.
                _autoApplyTimer.Stop();
            }
        }

        InitLaunchGroups();
        InitChatAlerts();

        Log.Info("EveDeck started.");
        _ = CheckForUpdateAsync();
        InitProfileCopy();
        InitMumbleBridge();
    }

    partial void InitProfileCopy();
    partial void InitMumbleBridge();

    private async Task CheckForUpdateAsync(bool manual = false)
    {
        if (manual)
        {
            _isCheckingForUpdate = true;
            UpdateCheckStatusText = "Checking for updates...";
            OnPropertyChanged(nameof(UpdateCheckStatusText));
            OnPropertyChanged(nameof(IsCheckingForUpdate));
        }

        UpdateCheckService.UpdateInfo? info = null;
        var current = Assembly.GetExecutingAssembly().GetName().Version;
        if (current is not null)
        {
            var currentStr = $"{current.Major}.{current.Minor}.{current.Build}";
            info = await new UpdateCheckService(Log).CheckAsync(currentStr);
            if (info is not null) _availableUpdate = info;
        }

        App.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(ShowUpdateBanner));
            OnPropertyChanged(nameof(UpdateVersionText));
            if (!manual) return;
            _isCheckingForUpdate = false;
            UpdateCheckStatusText = info is not null ? $"EveDeck {info.Version} is available" : "You're up to date.";
            OnPropertyChanged(nameof(UpdateCheckStatusText));
            OnPropertyChanged(nameof(IsCheckingForUpdate));
        });
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { } update) return;

        var kind = new UpdateApplyService(Log).DetectInstallKind();
        var canApplyInPlace = kind switch
        {
            InstallKind.Velopack => true,
            InstallKind.Inno => update.InstallerUrl is not null,
            _ => false
        };

        if (!canApplyInPlace)
        {
            if (update.DownloadUrl is { } url)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error($"Could not open link: {ex.Message}"); }
            return;
        }

        var confirm = MessageBox.Show(
            $"EveDeck will close and update to v{update.Version}. Continue?",
            "Update EveDeck",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var apply = new UpdateApplyService(Log);
            if (kind == InstallKind.Inno)
                await apply.ApplyInnoUpdateAsync(update.InstallerUrl!);
            else
                await apply.ApplyVelopackUpdateAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Update failed: {ex.Message}");
            MessageBox.Show($"The update could not be applied: {ex.Message}", "Update EveDeck", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Observables ────────────────────────────────────────────────────────────

    public LogService Log { get; }
    public ObservableCollection<EveWindowInfo> Windows { get; } = new();
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();
    public ObservableCollection<SlotAssignment> Assignments { get; }
    public ObservableCollection<LayoutProfile> Profiles { get; }
    public ICollectionView ProfilesView { get; }
    public ICollectionView WindowsView { get; }
    public ObservableCollection<HotkeyBinding> Hotkeys { get; }
    public ObservableCollection<LogEntry> Logs { get; }
    public ICollectionView LogsView { get; }  // 2h
    public ObservableCollection<Models.CharacterSet> CharacterSets => _settings.CharacterSets;
    public ObservableCollection<LayoutSlotPreview> LayoutPreviewSlots { get; } = new();
    public ObservableCollection<MiniMapSlot> MiniMapSlots { get; } = new();
    public ObservableCollection<MonitorPreviewItem> MonitorPreviewItems { get; } = new();
    public event EventHandler? HotkeysChanged;

    // ── Commands ───────────────────────────────────────────────────────────────

    public RelayCommand RefreshCommand { get; }
    public RelayCommand AssignSelectedCommand { get; }
    public RelayCommand AssignWindowToSlotCommand { get; }
    public RelayCommand RemoveWindowFromSlotCommand { get; }
    public RelayCommand ClearAssignmentCommand { get; }
    public RelayCommand ClearSlotCommand { get; }
    public RelayCommand FocusSlotCommand { get; }
    public RelayCommand AddSlotCommand { get; }
    public RelayCommand DeleteSelectedSlotCommand { get; }
    public RelayCommand AutoAssignAllCommand { get; }      // 2e
    public RelayCommand ApplyProfileCommand { get; }
    public RelayCommand CaptureProfileCommand { get; }
    public RelayCommand UndoLastApplyCommand { get; }      // 3b
    public RelayCommand SaveCommand { get; }
    public RelayCommand RestoreSelectedStyleCommand { get; }
    public RelayCommand ToggleSelectedBorderlessCommand { get; }
    public RelayCommand NewProfileCommand { get; }
    public RelayCommand EditLayoutCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand ImportProfileCommand { get; }
    public RelayCommand ExportProfileCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand CaptureHotkeyCommand { get; }
    public RelayCommand ClearHotkeyCommand { get; }
    public RelayCommand ResetHotkeysCommand { get; }       // 2c
    public RelayCommand SetMasterSlotCommand { get; }
    public RelayCommand AddEsiCharacterCommand { get; }
    public RelayCommand RemoveEsiCharacterCommand { get; }
    public RelayCommand RestoreBackupCommand { get; }
    public RelayCommand SpawnTestWindowsCommand { get; }
    public RelayCommand AutoSelectBestProfileCommand { get; }
    public RelayCommand SetMasterResolutionCommand { get; }
    public RelayCommand ClearMasterResolutionCommand { get; }
    public RelayCommand ToggleTopmostCommand { get; }
    public RelayCommand AddCharacterSetCommand { get; }
    public RelayCommand DeleteCharacterSetCommand { get; }
    public RelayCommand SwitchCharacterSetCommand { get; }
    public RelayCommand LaunchGroupCommand { get; }
    public RelayCommand AddChatAlertRuleCommand { get; }
    public RelayCommand RemoveChatAlertRuleCommand { get; }
    public RelayCommand AddGameEventRuleCommand { get; }
    public RelayCommand RemoveGameEventRuleCommand { get; }
    public RelayCommand AddOverlayAllowedAppCommand { get; }
    public RelayCommand RemoveOverlayAllowedAppCommand { get; }

    // ── Master resolution picker (VSR/DSR supersampling) ───────────────────────

    private ObservableCollection<DisplayModeOption> _availableMasterResolutions = new();
    public ObservableCollection<DisplayModeOption> AvailableMasterResolutions
    {
        get => _availableMasterResolutions;
        private set { _availableMasterResolutions = value; OnPropertyChanged(); }
    }

    private DisplayModeOption? _selectedMasterResolution;
    public DisplayModeOption? SelectedMasterResolution
    {
        get => _selectedMasterResolution;
        set
        {
            if (SetProperty(ref _selectedMasterResolution, value))
                SetMasterResolutionCommand.RaiseCanExecuteChanged();
        }
    }

    public string MasterResolutionStatus
    {
        get
        {
            if (SelectedProfile is null) return "No profile selected.";
            if (SelectedProfile.MasterResolutionWidth > 0)
            {
                var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
                    ?? Monitors.FirstOrDefault(m => m.IsPrimary);
                var w = SelectedProfile.MasterResolutionWidth;
                var h = SelectedProfile.MasterResolutionHeight;
                if (monitor is not null && (w > monitor.Bounds.Width || h > monitor.Bounds.Height))
                    return $"Warning: stored override {w}x{h} is larger than your current desktop ({monitor.Bounds.Width}x{monitor.Bounds.Height}) and will be clamped. To use {w}x{h}: enable AMD VSR or Nvidia DSR, switch Windows display resolution to {w}x{h}, then apply layout.";
                return $"Override active: master slot will be placed at {w}x{h}. Set EVE Fixed Window to match, restart clients, then click Apply Layout.";
            }
            return "Auto — master slot scales to fill the target monitor. The picker above shows all modes that fit your current desktop. To unlock higher VSR/DSR resolutions, switch your Windows display resolution to the virtual size first.";
        }
    }

    // "warn" | "active" | "info" — drives color triggers in Display Tips XAML.
    public string MasterResolutionStatusSeverity
    {
        get
        {
            if (SelectedProfile is null) return "info";
            if (SelectedProfile.MasterResolutionWidth > 0)
            {
                var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
                    ?? Monitors.FirstOrDefault(m => m.IsPrimary);
                var w = SelectedProfile.MasterResolutionWidth;
                var h = SelectedProfile.MasterResolutionHeight;
                if (monitor is not null && (w > monitor.Bounds.Width || h > monitor.Bounds.Height))
                    return "warn";
                return "active";
            }
            return "info";
        }
    }

    private void LoadMasterResolutions()
    {
        var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();

        if (monitor is null) return;

        var desktopW = monitor.Bounds.Width;
        var desktopH = monitor.Bounds.Height;

        var seenNative = new HashSet<(int, int)>();
        var seenVsr = new HashSet<(int, int)>();
        var modesNative = new List<DisplayModeOption>();
        var modesVsr = new List<DisplayModeOption>();
        var dm = new Utilities.Win32Native.DEVMODE();
        dm.dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<Utilities.Win32Native.DEVMODE>();
        dm.dmDeviceName = "";
        dm.dmFormName = "";
        uint modeNum = 0;
        while (Utilities.Win32Native.EnumDisplaySettingsEx(monitor.DeviceName, modeNum, ref dm, 0))
        {
            if (dm.dmBitsPerPel == 32 && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
            {
                int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight;
                if (w <= desktopW && h <= desktopH)
                {
                    if (seenNative.Add((w, h)))
                        modesNative.Add(new DisplayModeOption($"{w}×{h}", w, h));
                }
                else
                {
                    // Above desktop bounds: VSR/DSR virtual resolution — desktop must be switched to this
                    // resolution in Windows Display Settings before EveDeck can place windows at that size.
                    if (seenVsr.Add((w, h)))
                        modesVsr.Add(new DisplayModeOption($"{w}×{h} (switch desktop to use)", w, h));
                }
            }
            modeNum++;
        }

        // Build final list: current desktop first, then larger-area VSR/DSR options, then remaining native.
        var desktopLabel = $"{desktopW}×{desktopH} (current desktop)";
        var desktopMode = new DisplayModeOption(desktopLabel, desktopW, desktopH);

        var all = new List<DisplayModeOption> { desktopMode };
        all.AddRange(modesVsr.OrderByDescending(m => (long)m.Width * m.Height));
        all.AddRange(modesNative
            .Where(m => !(m.Width == desktopW && m.Height == desktopH))
            .OrderByDescending(m => (long)m.Width * m.Height));

        AvailableMasterResolutions = new ObservableCollection<DisplayModeOption>(all);

        // Pre-select the item that matches the profile's stored override (if any).
        if (SelectedProfile?.MasterResolutionWidth > 0)
            _selectedMasterResolution = all.FirstOrDefault(m => m.Width == SelectedProfile.MasterResolutionWidth && m.Height == SelectedProfile.MasterResolutionHeight);
        else
            _selectedMasterResolution = desktopMode;
        OnPropertyChanged(nameof(SelectedMasterResolution));
    }

    private void ExecuteSetMasterResolution()
    {
        if (SelectedMasterResolution is null || SelectedProfile is null) return;
        SelectedProfile.MasterResolutionWidth = SelectedMasterResolution.Width;
        SelectedProfile.MasterResolutionHeight = SelectedMasterResolution.Height;
        OnPropertyChanged(nameof(MasterResolutionStatus));
        OnPropertyChanged(nameof(MasterResolutionStatusSeverity));
        ClearMasterResolutionCommand.RaiseCanExecuteChanged();
        Save();
        Log.Info($"Master resolution override set to {SelectedMasterResolution.Width}×{SelectedMasterResolution.Height} for profile '{SelectedProfile.Name}'.");
    }

    private void ExecuteClearMasterResolution()
    {
        if (SelectedProfile is null) return;
        SelectedProfile.MasterResolutionWidth = 0;
        SelectedProfile.MasterResolutionHeight = 0;
        LoadMasterResolutions();
        OnPropertyChanged(nameof(MasterResolutionStatus));
        OnPropertyChanged(nameof(MasterResolutionStatusSeverity));
        ClearMasterResolutionCommand.RaiseCanExecuteChanged();
        Save();
        Log.Info($"Master resolution override cleared for profile '{SelectedProfile.Name}' (auto-fill restored).");
    }

    // ── Read-only computed ─────────────────────────────────────────────────────

    public string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "dev" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public bool ShowUpdateBanner => _availableUpdate is not null && !_updateBannerDismissed;
    public string UpdateVersionText => _availableUpdate is not null ? $"EveDeck {_availableUpdate.Version} is available" : "";
    public ICommand DismissUpdateBannerCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand CheckForUpdateCommand { get; }
    public string UpdateCheckStatusText { get; private set; } = "";
    public bool IsCheckingForUpdate => _isCheckingForUpdate;

    public bool ShowConfigResetBanner => _configService.WasResetFromCorruption && !_configResetBannerDismissed;
    public ICommand DismissConfigResetBannerCommand { get; }

    public bool ShowHotkeyConflictBanner => _hotkeyConflicts.Count > 0 && !_hotkeyConflictBannerDismissed;
    public string HotkeyConflictMessage => _hotkeyConflicts.Count == 1
        ? $"1 hotkey could not be registered: {_hotkeyConflicts[0]}"
        : $"{_hotkeyConflicts.Count} hotkeys could not be registered: {string.Join("; ", _hotkeyConflicts)}";
    public ICommand DismissHotkeyConflictBannerCommand { get; }

    // Called after every HotkeyService.RegisterAll so the banner reflects the current conflict set.
    public void SetHotkeyConflicts(IReadOnlyList<string> failures)
    {
        _hotkeyConflicts = failures;
        _hotkeyConflictBannerDismissed = false;
        OnPropertyChanged(nameof(ShowHotkeyConflictBanner));
        OnPropertyChanged(nameof(HotkeyConflictMessage));
    }

    public int WindowCount => Windows.Count;
    public int MonitorCount => Monitors.Count;
    public bool HasNoWindows => WindowCount == 0;
    public string DetectionStateText => WindowCount > 0 ? "EVE detected" : "No EVE windows";
    public string DetectionStateColor => WindowCount > 0 ? "#22C55E" : "#F59E0B";

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        set => SetProperty(ref _lastUpdatedText, value);
    }

    public int MasterSlotNumber => ActiveMasterSeat;

    // Transient promotion set by EnsureValidMasterSeat when the persisted master seat's client isn't
    // running, so the master geometry slot gets filled by a logged-in seat during a partial session.
    // NEVER persisted — the user's chosen master (SelectedProfile.MasterSeat) is untouched, so it
    // snaps back automatically on the next apply once the real master's client is running again.
    private int? _promotedMasterSeat;

    // The master seat of the ACTIVE profile (per-profile so each activity can centre a different main).
    // Falls back to the geometric centre slot when the profile hasn't designated one (new/migrated).
    // A live transient promotion (partial session) wins over the persisted value for placement/labels.
    internal int ActiveMasterSeat
    {
        get
        {
            if (_promotedMasterSeat is int promoted) return promoted;
            if (SelectedProfile is null) return _settings.MasterSlotNumber;
            return SelectedProfile.MasterSeat > 0 ? SelectedProfile.MasterSeat : CenterSlotNumber;
        }
        set
        {
            _promotedMasterSeat = null;   // an explicit master change supersedes any transient promotion
            if (SelectedProfile is not null) SelectedProfile.MasterSeat = value;
            else _settings.MasterSlotNumber = value;
            OnPropertyChanged(nameof(MasterSlotNumber));
            RaiseIdentityDependents();
        }
    }

    public ObservableCollection<LayoutSlot>? ActiveProfileSlots => SelectedProfile?.Slots;
    public bool SelectedProfileIsBuiltIn => SelectedProfile?.IsBuiltIn == true;
    public bool SelectedProfileIsFamilyTemplate => SelectedProfile?.IsFamilyTemplate == true;

    // Resolution/account-count dropdown options for whichever family is selected.
    public IReadOnlyList<DisplayModeOption> AvailableFamilyResolutions => SelectedProfile?.Category switch
    {
        "Grid" => PresetFactory.GridResolutionOptions,
        "Center Master" => PresetFactory.CenterMasterResolutionOptions,
        "Whammy Board" => PresetFactory.WhammyResolutionOptions,
        "Side Stack" => PresetFactory.SideStackResolutionOptions,
        "Twin Stack" => PresetFactory.TwinStackResolutionOptions,
        _ => Array.Empty<DisplayModeOption>(),
    };

    public IReadOnlyList<int> AvailableFamilyCounts => SelectedProfile?.Category switch
    {
        "Grid" => PresetFactory.GridCountOptions,
        "Center Master" => PresetFactory.CenterMasterCountOptions,
        "Whammy Board" => PresetFactory.WhammyCountOptions,
        "Side Stack" => PresetFactory.SideStackCountOptions,
        "Twin Stack" => PresetFactory.TwinStackCountOptions,
        _ => Array.Empty<int>(),
    };

    // Side (Left/Right) dropdown — only the Side Stack family has one.
    public bool SelectedProfileHasFamilySide =>
        SelectedProfile?.IsFamilyTemplate == true && SelectedProfile.Category == "Side Stack";

    public IReadOnlyList<string> AvailableFamilySides => PresetFactory.SideStackSideOptions;

    public string? SelectedFamilySide
    {
        get => SelectedProfile is null ? null
            : AvailableFamilySides.FirstOrDefault(s => s == SelectedProfile.TemplateSide) ?? AvailableFamilySides[0];
        set
        {
            if (SelectedProfile is null || value is null || SelectedProfile.TemplateSide == value) return;
            SelectedProfile.TemplateSide = value;
            PresetFactory.RegenerateFamilySlots(SelectedProfile);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveProfileSlots));
            UpdatePositionCodes();
            RebuildLayoutPreview();
            Save();
        }
    }

    public DisplayModeOption? SelectedFamilyResolution
    {
        get => SelectedProfile is null ? null
            : AvailableFamilyResolutions.FirstOrDefault(o => o.Width == SelectedProfile.TemplateWidth && o.Height == SelectedProfile.TemplateHeight);
        set
        {
            if (SelectedProfile is null || value is null) return;
            if (SelectedProfile.TemplateWidth == value.Width && SelectedProfile.TemplateHeight == value.Height) return;
            SelectedProfile.TemplateWidth = value.Width;
            SelectedProfile.TemplateHeight = value.Height;
            PresetFactory.RegenerateFamilySlots(SelectedProfile);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveProfileSlots));
            UpdatePositionCodes();
            RebuildLayoutPreview();
            Save();
        }
    }

    public int SelectedFamilyCount
    {
        get => SelectedProfile?.TemplateCount ?? 0;
        set
        {
            if (SelectedProfile is null || SelectedProfile.TemplateCount == value) return;
            SelectedProfile.TemplateCount = value;
            PresetFactory.RegenerateFamilySlots(SelectedProfile);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveProfileSlots));
            UpdatePositionCodes();
            RebuildLayoutPreview();
            Save();
        }
    }

    // Per-profile "Avoid taskbar" — fits THIS profile into the monitor work area at apply time.
    // Persisted on the profile itself so full-screen and taskbar-aware variants can coexist.
    public bool SelectedProfileAvoidTaskbar
    {
        get => SelectedProfile?.AvoidTaskbar == true;
        set
        {
            if (SelectedProfile is null || SelectedProfile.AvoidTaskbar == value) return;
            SelectedProfile.AvoidTaskbar = value;
            OnPropertyChanged();
            Save();
            ApplyActiveProfile();
        }
    }

    public bool IsCapturingHotkey => _capturingHotkey is not null;

    public Models.CharacterSet? ActiveCharacterSet
        => _settings.CharacterSets.FirstOrDefault(s => s.Id == _settings.ActiveCharacterSetId);

    public string ActiveCharacterSetId => _settings.ActiveCharacterSetId;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    // ── Settings-backed properties ─────────────────────────────────────────────

    public bool UsePhysicalPixels
    {
        get => _settings.UsePhysicalPixels;
        set
        {
            if (_settings.UsePhysicalPixels == value) return;
            _settings.UsePhysicalPixels = value;
            OnPropertyChanged();
            Log.Warn(value
                ? "Using physical pixels. Coordinates should match screenshots and Win32 window rectangles."
                : "Using scaled logical coordinates. Windows scaling may change coordinate behavior.");
            Save();
        }
    }

    public bool IncludeNotepadTestWindows
    {
        get => _settings.IncludeNotepadTestWindows;
        set
        {
            if (_settings.IncludeNotepadTestWindows == value) return;
            _settings.IncludeNotepadTestWindows = value;
            OnPropertyChanged();
            Save();
            Refresh();
        }
    }

    public bool AutoRefresh
    {
        get => _settings.AutoRefresh;
        set
        {
            if (_settings.AutoRefresh == value) return;
            _settings.AutoRefresh = value;
            OnPropertyChanged();
            if (value) _refreshTimer.Start(); else _refreshTimer.Stop();
            Save();
        }
    }

    public string LayoutTargetMonitorId
    {
        get => _settings.LayoutTargetMonitorId;
        set
        {
            if (_settings.LayoutTargetMonitorId == value) return;
            _settings.LayoutTargetMonitorId = value;
            OnPropertyChanged();
            LoadMasterResolutions();
            OnPropertyChanged(nameof(MasterResolutionStatus));
            OnPropertyChanged(nameof(MasterResolutionStatusSeverity));
            Save();
        }
    }

    public bool UseMonitorWorkArea
    {
        get => _settings.UseMonitorWorkArea;
        set
        {
            if (_settings.UseMonitorWorkArea == value) return;
            _settings.UseMonitorWorkArea = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool ActiveFrameEnabled
    {
        get => _settings.ActiveFrameEnabled;
        set
        {
            if (_settings.ActiveFrameEnabled == value) return;
            _settings.ActiveFrameEnabled = value;
            OnPropertyChanged();
            if (value) StartFrameOverlay(); else StopFrameOverlay();
            Save();
        }
    }

    public int ActiveFrameThickness
    {
        get => _settings.ActiveFrameThickness;
        set
        {
            var clamped = Math.Clamp(value, 1, 20);
            if (_settings.ActiveFrameThickness == clamped) return;
            _settings.ActiveFrameThickness = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public int ActiveFrameGlowRadius
    {
        get => _settings.ActiveFrameGlowRadius;
        set
        {
            var clamped = Math.Clamp(value, 1, 40);
            if (_settings.ActiveFrameGlowRadius == clamped) return;
            _settings.ActiveFrameGlowRadius = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public string ActiveFrameColor
    {
        get => _settings.ActiveFrameColor;
        set
        {
            if (_settings.ActiveFrameColor == value) return;
            _settings.ActiveFrameColor = value;
            _frameBrush = ParseFrameBrush(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveFrameBrush));
            Save();
        }
    }

    // Swatch preview for the Options-tab color picker button.
    public Brush ActiveFrameBrush => ParseFrameBrush(ActiveFrameColor);

    // 2a — Minimize to system tray instead of taskbar.
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (_settings.MinimizeToTray == value) return;
            _settings.MinimizeToTray = value;
            OnPropertyChanged();
            Save();
        }
    }

    // 2g — Apply a specified profile on startup.
    public bool ApplyProfileOnStartup
    {
        get => _settings.ApplyProfileOnStartup;
        set
        {
            if (_settings.ApplyProfileOnStartup == value) return;
            _settings.ApplyProfileOnStartup = value;
            OnPropertyChanged();
            Save();
        }
    }

    public string StartupProfileId
    {
        get => _settings.StartupProfileId;
        set
        {
            if (_settings.StartupProfileId == value) return;
            _settings.StartupProfileId = value;
            OnPropertyChanged();
            Save();
        }
    }

    // Re-apply the active profile automatically when assigned clients (re)appear.
    public bool AutoApplyOnClientLaunch
    {
        get => _settings.AutoApplyOnClientLaunch;
        set
        {
            if (_settings.AutoApplyOnClientLaunch == value) return;
            _settings.AutoApplyOnClientLaunch = value;
            OnPropertyChanged();
            if (!value) _autoApplyTimer.Stop();
            Save();
        }
    }

    // 2j — Launch EveDeck with Windows via Run registry key.
    public bool LaunchWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("EveDeck") is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (value)
            {
                var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                key.SetValue("EveDeck", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("EveDeck", throwOnMissingValue: false);
            }
            OnPropertyChanged();
            Log.Info(value ? "Added EveDeck to Windows startup." : "Removed EveDeck from Windows startup.");
        }
    }

    // 2h — Log level filter ("All", "Info", "Warn", "Error").
    public string LogFilterLevel
    {
        get => _logFilterLevel;
        set
        {
            if (SetProperty(ref _logFilterLevel, value))
            {
                LogsView.Filter = value == "All"
                    ? null
                    : o => o is LogEntry e && e.Level.Equals(value, StringComparison.OrdinalIgnoreCase);
                LogsView.Refresh();
            }
        }
    }

    // 2d — Profile name search filter.
    public string ProfileSearchText
    {
        get => _profileSearchText;
        set
        {
            if (SetProperty(ref _profileSearchText, value))
            {
                ProfilesView.Filter = string.IsNullOrWhiteSpace(value)
                    ? null
                    : o => o is LayoutProfile p && p.Name.Contains(value, StringComparison.OrdinalIgnoreCase);
                ProfilesView.Refresh();
            }
        }
    }

    public bool CornerOverlaysEnabled
    {
        get => _settings.CornerOverlaysEnabled;
        set
        {
            if (_settings.CornerOverlaysEnabled == value) return;
            _settings.CornerOverlaysEnabled = value;
            OnPropertyChanged();
            UpdatePositionCodes();
            if (!value) StopCornerOverlays();
            Save();
        }
    }

    public bool CornerOverlayShowLabel
    {
        get => _settings.CornerOverlayShowLabel;
        set
        {
            if (_settings.CornerOverlayShowLabel == value) return;
            _settings.CornerOverlayShowLabel = value;
            OnPropertyChanged();
            Save();
            if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    public bool FocusPreviewOnClick
    {
        get => _settings.FocusPreviewOnClick;
        set
        {
            if (_settings.FocusPreviewOnClick == value) return;
            _settings.FocusPreviewOnClick = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool HoverPreviewEnabled
    {
        get => _settings.HoverPreviewEnabled;
        set
        {
            if (_settings.HoverPreviewEnabled == value) return;
            _settings.HoverPreviewEnabled = value;
            OnPropertyChanged();
            Save();
        }
    }

    public int HoverPreviewDelayMs
    {
        get => _settings.HoverPreviewDelayMs;
        set
        {
            var clamped = Math.Max(0, Math.Min(2000, value));
            if (_settings.HoverPreviewDelayMs == clamped) return;
            _settings.HoverPreviewDelayMs = clamped;
            _hoverPeekTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, clamped));
            OnPropertyChanged();
            Save();
        }
    }

    public string HoverPreviewStyle
    {
        get => _settings.HoverPreviewStyle;
        set
        {
            if (_settings.HoverPreviewStyle == value || string.IsNullOrEmpty(value)) return;
            _settings.HoverPreviewStyle = value;
            OnPropertyChanged();
            Save();
        }
    }

    public double HoverZoomFactor
    {
        get => _settings.HoverZoomFactor;
        set
        {
            var clamped = Math.Clamp(value, 1.5, 4.0);
            if (Math.Abs(_settings.HoverZoomFactor - clamped) < 0.01) return;
            _settings.HoverZoomFactor = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public bool CornerOverlayShowSlotNumber
    {
        get => _settings.CornerOverlayShowSlotNumber;
        set
        {
            if (_settings.CornerOverlayShowSlotNumber == value) return;
            _settings.CornerOverlayShowSlotNumber = value;
            OnPropertyChanged();
            Save();
            if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) RefreshAllPills();
        }
    }

    public bool CornerOverlayShowSystem
    {
        get => _settings.CornerOverlayShowSystem;
        set
        {
            if (_settings.CornerOverlayShowSystem == value) return;
            _settings.CornerOverlayShowSystem = value;
            OnPropertyChanged();
            Save();
            if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) RefreshAllPills();
        }
    }

    public double CornerOverlayLabelFontSize
    {
        get => _settings.CornerOverlayLabelFontSize;
        set
        {
            var clamped = Math.Clamp(value, 6.0, 72.0);
            if (Math.Abs(_settings.CornerOverlayLabelFontSize - clamped) < 0.1) return;
            _settings.CornerOverlayLabelFontSize = clamped; OnPropertyChanged(); Save();
            if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    public string CornerOverlayLabelStyle
    {
        get => _settings.CornerOverlayLabelStyle;
        set
        {
            if (_settings.CornerOverlayLabelStyle == value || string.IsNullOrEmpty(value)) return;
            _settings.CornerOverlayLabelStyle = value;
            OnPropertyChanged();
            Save();
            if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    // One-line summary of the global default label font for the Options tab (e.g. "Segoe UI, 13px").
    public string LabelFontSummary
    {
        get
        {
            var family = string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelFontFamily)
                ? "Segoe UI" : _settings.CornerOverlayLabelFontFamily;
            return $"{family}, {_settings.CornerOverlayLabelFontSize:0}px";
        }
    }

    // Current global default label font (family, WPF DIP size, colour hex) for seeding the font dialog.
    public (string family, double sizeDip, string color) GlobalLabelFont() =>
        (_settings.CornerOverlayLabelFontFamily ?? "", _settings.CornerOverlayLabelFontSize, _settings.CornerOverlayLabelColor ?? "");

    // Applies the global DEFAULT label font (family + size + colour) chosen in the WinForms font dialog.
    // Size is a WPF DIP value already (the caller converts from the dialog's points). Rebuilds overlays.
    public void ApplyGlobalLabelFont(string family, double sizeDip, string colorHex)
    {
        _settings.CornerOverlayLabelFontFamily = family ?? "";
        _settings.CornerOverlayLabelFontSize = Math.Clamp(sizeDip, 6.0, 72.0);
        if (!string.IsNullOrWhiteSpace(colorHex)) _settings.CornerOverlayLabelColor = colorHex;
        OnPropertyChanged(nameof(CornerOverlayLabelFontSize));
        OnPropertyChanged(nameof(LabelFontSummary));
        Save();
        if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
    }

    // Applies (or clears, when args are null) a single seat's label font overrides. Rebuilds overlays.
    public void ApplySeatLabelFont(SlotAssignment seat, string? family, double? sizeDip, string? colorHex)
    {
        seat.LabelFontFamily = string.IsNullOrWhiteSpace(family) ? null : family;
        seat.LabelFontSize = sizeDip.HasValue ? Math.Clamp(sizeDip.Value, 6.0, 72.0) : null;
        seat.LabelColor = string.IsNullOrWhiteSpace(colorHex) ? null : colorHex;
        Save();
        if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
    }

    // One-line summary of the MASTER label font for the Options tab — shows the concrete effective
    // values whether they're explicitly set or inherited from the normal default font.
    public string MasterLabelFontSummary
    {
        get
        {
            var family = string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelFontFamilyMaster)
                ? (string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelFontFamily) ? "Segoe UI" : _settings.CornerOverlayLabelFontFamily)
                : _settings.CornerOverlayLabelFontFamilyMaster;
            var size = _settings.CornerOverlayLabelFontSizeMaster ?? _settings.CornerOverlayLabelFontSize;
            return $"{family}, {size:0}px";
        }
    }

    // Current global MASTER label font (family, WPF DIP size, colour hex) for seeding the font
    // dialog — falls back to the normal default's concrete values when unset.
    public (string family, double sizeDip, string color) GlobalMasterLabelFont() =>
        (string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelFontFamilyMaster) ? _settings.CornerOverlayLabelFontFamily : _settings.CornerOverlayLabelFontFamilyMaster,
         _settings.CornerOverlayLabelFontSizeMaster ?? _settings.CornerOverlayLabelFontSize,
         string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelColorMaster) ? _settings.CornerOverlayLabelColor : _settings.CornerOverlayLabelColorMaster);

    // Applies the global MASTER label font (family + size + colour) chosen in the WinForms font dialog.
    public void ApplyGlobalMasterLabelFont(string family, double sizeDip, string colorHex)
    {
        _settings.CornerOverlayLabelFontFamilyMaster = family ?? "";
        _settings.CornerOverlayLabelFontSizeMaster = Math.Clamp(sizeDip, 6.0, 72.0);
        if (!string.IsNullOrWhiteSpace(colorHex)) _settings.CornerOverlayLabelColorMaster = colorHex;
        OnPropertyChanged(nameof(MasterLabelFontSummary));
        Save();
        if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
    }

    // Clears the global MASTER label font override so it inherits the normal default again.
    public void ResetGlobalMasterLabelFont()
    {
        _settings.CornerOverlayLabelFontFamilyMaster = "";
        _settings.CornerOverlayLabelFontSizeMaster = null;
        _settings.CornerOverlayLabelColorMaster = "";
        OnPropertyChanged(nameof(MasterLabelFontSummary));
        Save();
        if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
    }

    // Applies (or clears, when args are null) a single seat's MASTER label font overrides.
    public void ApplySeatMasterLabelFont(SlotAssignment seat, string? family, double? sizeDip, string? colorHex)
    {
        seat.LabelFontFamilyMaster = string.IsNullOrWhiteSpace(family) ? null : family;
        seat.LabelFontSizeMaster = sizeDip.HasValue ? Math.Clamp(sizeDip.Value, 6.0, 72.0) : null;
        seat.LabelColorMaster = string.IsNullOrWhiteSpace(colorHex) ? null : colorHex;
        Save();
        if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
    }

    // Current effective active-window frame colour for a seat (its own override, else the global) --
    // used to seed the per-seat colour picker.
    public string EffectiveSeatFrameColor(SlotAssignment seat)
        => string.IsNullOrWhiteSpace(seat.FrameColor) ? ActiveFrameColor : seat.FrameColor!;

    // Applies (or clears, when colorHex is null) a single seat's active-window frame colour override.
    public void ApplySeatFrameColor(SlotAssignment seat, string? colorHex)
    {
        seat.FrameColor = string.IsNullOrWhiteSpace(colorHex) ? null : colorHex;
        _lastFrameHandle = 0; // force the frame overlay to re-resolve this seat's colour on the next tick
        Save();
    }

    // Applies both halves of a pasted EVE theme string to a seat in one go: Primary -> frame colour,
    // Accent -> label colour (both the normal/alt pill and the Master pill, so the pasted theme
    // reads consistently regardless of which one is currently showing for this seat) (see
    // Utilities.EveThemeString, used by the "Paste Theme" seat button).
    public void ApplySeatTheme(SlotAssignment seat, string frameColorHex, string labelColorHex)
    {
        seat.FrameColor = frameColorHex;
        seat.LabelColor = labelColorHex;
        seat.LabelColorMaster = labelColorHex;
        _lastFrameHandle = 0;
        Save();
        if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
    }

    // Last main-window position; persisted by the view on close and restored on launch.
    public double? WindowLeft
    {
        get => _settings.WindowLeft;
        set => _settings.WindowLeft = value;
    }

    public double? WindowTop
    {
        get => _settings.WindowTop;
        set => _settings.WindowTop = value;
    }

    public bool RequireEveFocusForHotkeys
    {
        get => _settings.RequireEveFocusForHotkeys;
        set
        {
            if (_settings.RequireEveFocusForHotkeys == value) return;
            _settings.RequireEveFocusForHotkeys = value;
            OnPropertyChanged();
            Save();
            // Re-register so gated hotkeys start/stop following the foreground window.
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool ThrottleBackgroundProcesses
    {
        get => _settings.ThrottleBackgroundProcesses;
        set
        {
            if (_settings.ThrottleBackgroundProcesses == value) return;
            _settings.ThrottleBackgroundProcesses = value;
            OnPropertyChanged();
            if (!value) RestoreAllProcessPriorities();
            Save();
        }
    }

    public bool AutoMinimizeInactiveClients
    {
        get => _settings.AutoMinimizeInactiveClients;
        set
        {
            if (_settings.AutoMinimizeInactiveClients == value) return;
            _settings.AutoMinimizeInactiveClients = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool HideActiveSeatTile
    {
        get => _settings.HideActiveSeatTile;
        set
        {
            if (_settings.HideActiveSeatTile == value) return;
            _settings.HideActiveSeatTile = value;
            OnPropertyChanged();
            Save();
        }
    }

    public int OfflineOverlayTimeoutSeconds
    {
        get => _settings.OfflineOverlayTimeoutSeconds;
        set
        {
            var clamped = Math.Max(0, value);
            if (_settings.OfflineOverlayTimeoutSeconds == clamped) return;
            _settings.OfflineOverlayTimeoutSeconds = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public int OfflinePillTimeoutSeconds
    {
        get => _settings.OfflinePillTimeoutSeconds;
        set
        {
            var clamped = Math.Max(-1, value);
            if (_settings.OfflinePillTimeoutSeconds == clamped) return;
            _settings.OfflinePillTimeoutSeconds = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    internal void ApplyProcessPriorities(nint focusedHandle)
    {
        if (!_settings.ThrottleBackgroundProcesses) return;
        foreach (var w in Windows)
            _windowService.SetProcessPriority((uint)w.ProcessId, w.Handle != focusedHandle);
    }

    private void RestoreAllProcessPriorities()
    {
        foreach (var w in Windows)
            _windowService.SetProcessPriority((uint)w.ProcessId, false);
    }

    // Tracks the last EVE-foreground state so repeated foreground changes (e.g. tabbing between two EVE
    // clients) don't re-issue redundant SetWindowPos calls; only real EVE<->non-EVE transitions apply.
    private bool? _lastEveForeground;

    // Force a fresh apply next call (after layout apply / window (re)assignment the cache is stale).
    internal void ApplyTopmostState()
    {
        _lastEveForeground = null;
        RefreshTopmostForForeground(_windowService.GetForegroundWindowHandle());
    }

    // Focus-gated always-on-top: a pinned seat's window is HWND_TOPMOST only while an EVE client is the
    // foreground app, so pinned windows float over EVE but drop out of the way when you switch to a
    // non-EVE app. Driven by the foreground WinEvent hook (HotkeyService.ForegroundChanged).
    internal void RefreshTopmostForForeground(nint foregroundHwnd)
    {
        var eveForeground = foregroundHwnd != 0 && Windows.Any(w => w.Handle == foregroundHwnd);
        if (_lastEveForeground == eveForeground) return;
        _lastEveForeground = eveForeground;

        foreach (var seat in Assignments)
        {
            if (!seat.IsTopmost) continue;
            foreach (var w in FindAssignedWindows(seat))
                _windowService.SetWindowTopmost(w.Handle, eveForeground);
        }
    }

    private void ToggleTopmostForActive()
    {
        var fg = _windowService.GetForegroundWindowHandle();
        if (fg == 0) return;
        var seat = Assignments.FirstOrDefault(a => FindAssignedWindows(a).Any(w => w.Handle == fg));
        if (seat is null) return;
        seat.IsTopmost = !seat.IsTopmost;
        _windowService.SetWindowTopmost(fg, seat.IsTopmost);
        Save();
        Log.Info($"Seat {seat.SlotNumber} ({seat.Label}) always-on-top: {seat.IsTopmost}.");
    }

    // Called from hotkey dispatch (1-based index into CharacterSets).
    internal void SwitchCharacterSet(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > _settings.CharacterSets.Count) return;
        SwitchToCharacterSet(_settings.CharacterSets[oneBasedIndex - 1].Id);
    }

    private void SwitchToCharacterSet(string targetId)
    {
        if (targetId == _settings.ActiveCharacterSetId) return;
        var target = _settings.CharacterSets.FirstOrDefault(s => s.Id == targetId);
        if (target is null) return;

        // Snapshot the live collections back into the current set before leaving it.
        SnapshotLiveToActiveSet();

        // Update IsActive flag on all sets (used by UI toggle buttons).
        foreach (var s in _settings.CharacterSets) s.IsActive = false;
        target.IsActive = true;
        _settings.ActiveCharacterSetId = targetId;

        // Repopulate live collections from the target set.
        Assignments.Clear();
        foreach (var a in target.Assignments) Assignments.Add(a);
        Hotkeys.Clear();
        foreach (var h in target.Hotkeys) Hotkeys.Add(h);

        HotkeysChanged?.Invoke(this, EventArgs.Empty);
        SyncMasterSlot();
        UpdatePositionCodes();
        RaiseIdentityDependents();
        OnPropertyChanged(nameof(ActiveCharacterSet));
        OnPropertyChanged(nameof(ActiveCharacterSetId));
        Save();
        Log.Info($"Switched to character set '{target.Name}'.");
    }

    private void SnapshotLiveToActiveSet()
    {
        var active = ActiveCharacterSet;
        if (active is null) return;
        active.Assignments.Clear();
        foreach (var a in Assignments) active.Assignments.Add(a);
        active.Hotkeys.Clear();
        foreach (var h in Hotkeys) active.Hotkeys.Add(h);
    }

    private void AddCharacterSet()
    {
        SnapshotLiveToActiveSet();

        var newSet = new Models.CharacterSet
        {
            Name = $"Set {_settings.CharacterSets.Count + 1}"
        };
        // Clone seat structure (same slot numbers/labels) but clear window assignments.
        foreach (var a in Assignments)
        {
            newSet.Assignments.Add(new SlotAssignment
            {
                SlotNumber = a.SlotNumber,
                Label = a.Label,
                IsMaster = a.IsMaster,
                FrameColor = a.FrameColor,
                LabelFontFamily = a.LabelFontFamily,
                LabelFontSize = a.LabelFontSize,
                LabelColor = a.LabelColor,
                NeverMinimize = a.NeverMinimize
            });
        }
        // Clone hotkey bindings unbound (user should configure them per set).
        foreach (var h in Hotkeys)
            newSet.Hotkeys.Add(new HotkeyBinding
            {
                ActionId = h.ActionId,
                DisplayName = h.DisplayName,
                Modifiers = h.Modifiers,
                VirtualKey = h.VirtualKey,
                GestureText = h.GestureText,
                Enabled = h.Enabled,
                TargetCharacter = h.TargetCharacter
            });

        _settings.CharacterSets.Add(newSet);
        DeleteCharacterSetCommand.RaiseCanExecuteChanged();
        SwitchToCharacterSet(newSet.Id);
        Log.Info($"Added character set '{newSet.Name}'.");
    }

    private void DeleteCharacterSet(Models.CharacterSet set)
    {
        if (_settings.CharacterSets.Count <= 1) return;

        var wasActive = set.Id == _settings.ActiveCharacterSetId;
        _settings.CharacterSets.Remove(set);
        DeleteCharacterSetCommand.RaiseCanExecuteChanged();

        if (wasActive)
            SwitchToCharacterSet(_settings.CharacterSets[0].Id);
        else
        {
            Save();
            Log.Info($"Deleted character set '{set.Name}'.");
        }
    }

    // True when a managed EVE (or test) window currently owns the foreground. Used by the hotkey
    // service to decide whether gated hotkeys should be registered with the OS right now.
    public bool IsEveWindowForeground()
    {
        var fg = _windowService.GetForegroundWindowHandle();
        return fg != 0 && Windows.Any(w => w.Handle == fg);
    }

    public double UiScale
    {
        get => _settings.UiScale;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, 0.5, 3.0), 2);
            if (Math.Abs(_settings.UiScale - clamped) < 0.001) return;
            _settings.UiScale = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    // ── Selection properties ───────────────────────────────────────────────────

    public EveWindowInfo? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value))
                RaiseCommandStates();
        }
    }

    public SlotAssignment? SelectedAssignment
    {
        get => _selectedAssignment;
        set
        {
            if (SetProperty(ref _selectedAssignment, value))
                RaiseCommandStates();
        }
    }

    public LayoutProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && value is not null)
            {
                _settings.ActiveProfileId = value.Id;
                OnPropertyChanged(nameof(ActiveProfileSlots));
                OnPropertyChanged(nameof(SelectedProfileIsBuiltIn));
                OnPropertyChanged(nameof(SelectedProfileIsFamilyTemplate));
                OnPropertyChanged(nameof(AvailableFamilyResolutions));
                OnPropertyChanged(nameof(AvailableFamilyCounts));
                OnPropertyChanged(nameof(SelectedFamilyResolution));
                OnPropertyChanged(nameof(SelectedFamilyCount));
                OnPropertyChanged(nameof(SelectedProfileHasFamilySide));
                OnPropertyChanged(nameof(SelectedFamilySide));
                OnPropertyChanged(nameof(SelectedProfileAvoidTaskbar));
                OnPropertyChanged(nameof(MasterResolutionStatus));
                OnPropertyChanged(nameof(MasterResolutionStatusSeverity));
                SetMasterResolutionCommand.RaiseCanExecuteChanged();
                ClearMasterResolutionCommand.RaiseCanExecuteChanged();
                LoadMasterResolutions();
                // Master is per-profile: re-point the master badge + corner baseline at the new profile's.
                EnsureValidMasterSeat();
                SyncMasterSlot();
                ResetCornerOccupancy();
                OnPropertyChanged(nameof(MasterSlotNumber));
                UpdatePositionCodes();
                RebuildLayoutPreview();
                RaiseCommandStates();
            }
        }
    }

    // ── First-run setup ──────────────────────────────────────────────────────────

    public bool NeedsSetup => !_settings.SetupCompleted;

    // Recompute each assignment's positional code (Master / TL / TR / BL / BR / TC / BC / ...) from the
    // active profile's slot geometry. Used in the UI and overlay pills instead of a bare slot number.
    // Falls back to slot numbers when codes would collide (e.g. a stacked layout, all slots at 0,0).
    internal void UpdatePositionCodes()
    {
        var profile = SelectedProfile;
        if (profile is null || profile.Slots.Count == 0)
        {
            foreach (var a in Assignments) a.PositionCode = a.SlotNumber.ToString();
            return;
        }

        var slots = profile.Slots;
        var minX = slots.Min(s => s.X);
        var minY = slots.Min(s => s.Y);
        var totalW = Math.Max(1, slots.Max(s => s.X + s.Width) - minX);
        var totalH = Math.Max(1, slots.Max(s => s.Y + s.Height) - minY);

        // Pure geometric codes per position slot — no seat-identity overrides.
        var codes = new Dictionary<int, string>();
        foreach (var slot in slots)
            codes[slot.SlotNumber] = GridCode(slot, minX, minY, totalW, totalH);

        var hasCollision = codes.Values.GroupBy(c => c, StringComparer.Ordinal).Any(g => g.Count() > 1);

        // In corner-overlay mode with live occupancy, each seat card shows WHERE THAT SEAT'S
        // WINDOW CURRENTLY IS on screen: "Master" if centred, or the corner's geometric arrow.
        // This is correct even after swaps (e.g. Seat 1 sent to BL still shows ↙, not ↖).
        if (_settings.CornerOverlaysEnabled && _cornerSeatByGroup.Count > 0)
        {
            // Build a merged seat->currentPosition map across all groups.
            var seatToPosition = new Dictionary<int, int>();
            foreach (var (_, corners) in _cornerSeatByGroup)
                foreach (var (pos, seat) in corners)
                    seatToPosition[seat] = pos;

            // Determine which seats are centred in their respective group.
            var centeredSeats = new HashSet<int>(_centeredSeatByGroup.Values);

            foreach (var a in Assignments)
            {
                if (centeredSeats.Contains(a.SlotNumber))
                    a.PositionCode = "Master";
                else if (!hasCollision
                         && seatToPosition.TryGetValue(a.SlotNumber, out var pos)
                         && codes.TryGetValue(pos, out var posCode))
                    a.PositionCode = posCode;
                else
                    a.PositionCode = a.SlotNumber.ToString();
            }
            return;
        }

        // Flat / no-overlay: seat numbers double as position keys (identity home arrangement).
        foreach (var a in Assignments)
            a.PositionCode = (!hasCollision && codes.TryGetValue(a.SlotNumber, out var code))
                ? code
                : a.SlotNumber.ToString();
    }

    // Map a slot to a directional arrow symbol from its centre within the layout bounds.
    private static string GridCode(LayoutSlot slot, int minX, int minY, int totalW, int totalH)
    {
        var normX = (slot.X + slot.Width / 2.0 - minX) / totalW;
        var normY = (slot.Y + slot.Height / 2.0 - minY) / totalH;

        var col = normX < 1.0 / 3 ? "L" : normX < 2.0 / 3 ? "C" : "R";
        var row = normY < 1.0 / 3 ? "T" : normY < 2.0 / 3 ? "M" : "B";

        return (row, col) switch
        {
            ("T", "L") => "↖",
            ("T", "C") => "↑",
            ("T", "R") => "↗",
            ("M", "L") => "←",
            ("M", "C") => "⊕",
            ("M", "R") => "→",
            ("B", "L") => "↙",
            ("B", "C") => "↓",
            ("B", "R") => "↘",
            _ => row + col,
        };
    }

    // Returns the 3x3 zone bucket (row: T/M/B, col: L/C/R) for a slot's centre within the layout bounds.
    // Used by FocusDirection to resolve which slot occupies a given screen direction at runtime.
    private static (string row, string col) GridBucket(LayoutSlot slot, int minX, int minY, int totalW, int totalH)
    {
        var normX = (slot.X + slot.Width / 2.0 - minX) / totalW;
        var normY = (slot.Y + slot.Height / 2.0 - minY) / totalH;
        var col = normX < 1.0 / 3 ? "L" : normX < 2.0 / 3 ? "C" : "R";
        var row = normY < 1.0 / 3 ? "T" : normY < 2.0 / 3 ? "M" : "B";
        return (row, col);
    }

    // User dismissed the wizard without finishing — stop auto-showing it (re-runnable from Settings).
    public void DismissSetup()
    {
        if (_settings.SetupCompleted) return;
        _settings.SetupCompleted = true;
        Save();
        OnPropertyChanged(nameof(NeedsSetup));
    }

    // Apply the choices collected by the setup wizard: target monitor, an appropriately-sized
    // built-in profile for the client count, and (for 5-client) the corner-master configuration.
    public void RunInitialSetup(int clientCount, string monitorId, bool focusPreviewOnClick = true)
    {
        Refresh(); // make sure the monitor list is current before we resolve the selection

        if (!string.IsNullOrWhiteSpace(monitorId) && Monitors.Any(m => m.Id == monitorId))
            LayoutTargetMonitorId = monitorId;

        var mon = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var width = mon?.Bounds.Width ?? 2560;
        var height = mon?.Bounds.Height ?? 1440;

        var profile = ResolveAndApplyBestProfile(clientCount, width, height);

        if (clientCount == 5)
        {
            // Flagship corner-master layout: full-screen, master = centre slot 5, overlays on.
            UseMonitorWorkArea = false;
            FocusPreviewOnClick = focusPreviewOnClick;
            ActiveMasterSeat = 5;
            SyncMasterSlot();
            CornerOverlaysEnabled = true;
        }
        else
        {
            CornerOverlaysEnabled = false;
            ActiveMasterSeat = 1;
            SyncMasterSlot();
        }

        UpdatePositionCodes();
        _settings.SetupCompleted = true;
        Save();
        OnPropertyChanged(nameof(NeedsSetup));
        Log.Info($"Setup complete: {clientCount} client(s) on '{mon?.DeviceName ?? "?"}', profile '{profile?.Name ?? "none"}'.");

        // Rebuild corner occupancy with the new master and restart overlays so hover-peek works immediately.
        if (_settings.CornerOverlaysEnabled)
        {
            ResetCornerOccupancy();
            UpdatePositionCodes();
            StartCornerOverlays();
        }
    }

    // Spawn one Notepad window per slot so the user can test layouts without EVE running.
    // Each window is titled "EveDeck TEST - Slot N" so it's visually distinct in the window list.
    public void SpawnTestWindows()
    {
        var count = Assignments.Count;
        if (count == 0) { Status = "No slots configured — add slots first."; return; }

        IncludeNotepadTestWindows = true;

        var spawned = new List<System.Diagnostics.Process>();
        for (var i = 0; i < count; i++)
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            if (p is not null) spawned.Add(p);
        }

        // Wait for all windows to become ready, then rename them.
        Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(300);
                var allReady = spawned.All(p => { try { p.Refresh(); return p.MainWindowHandle != 0; } catch { return false; } });
                if (allReady) break;
            }

            for (var i = 0; i < spawned.Count; i++)
            {
                try
                {
                    spawned[i].Refresh();
                    var hwnd = spawned[i].MainWindowHandle;
                    if (hwnd != 0)
                        Utilities.Win32Native.SetWindowText(hwnd, $"EveDeck TEST - Slot {i + 1}");
                }
                catch { /* process may have exited */ }
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Refresh();
                Status = $"Spawned {spawned.Count} Notepad test window(s).";
                Log.Info($"Spawned {spawned.Count} Notepad test window(s) for layout testing.");
            });
        });
    }

    // Select the built-in profile that best matches the current slot count and target monitor.
    public void AutoSelectBestProfile()
    {
        var count = Assignments.Count;
        var mon = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var w = mon?.Bounds.Width ?? 2560;
        var h = mon?.Bounds.Height ?? 1440;

        var profile = ResolveAndApplyBestProfile(count, w, h);
        if (profile is null)
        {
            Status = $"No built-in profile found for {count} slot(s) at {w}x{h}.";
            return;
        }
        Status = $"Auto-selected profile: {profile.Name}";
        Log.Info($"Auto-selected profile '{profile.Name}' for {count} slot(s) on {w}x{h}.");
    }

    // Shared by AutoSelectBestProfile and RunInitialSetup: for 1 client, pick the fixed "1-Char {res}"
    // profile by name; for 2+, point the Grid/Center Master family template at the nearest curated
    // resolution+count and regenerate its slots, then select it. Returns null if nothing suitable exists.
    private LayoutProfile? ResolveAndApplyBestProfile(int clientCount, int monitorWidth, int monitorHeight)
    {
        if (clientCount == 1)
        {
            var name = PresetFactory.BestProfileName(clientCount, monitorWidth, monitorHeight);
            var solo = name is null ? null : Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (solo is not null) SelectedProfile = solo;
            return solo;
        }

        var sel = PresetFactory.ResolveFamilySelection(clientCount, monitorWidth, monitorHeight);
        if (sel is null) return null;

        var family = Profiles.FirstOrDefault(p => p.IsFamilyTemplate && p.Category == sel.Value.Category);
        if (family is null) return null;

        family.TemplateWidth = sel.Value.Width;
        family.TemplateHeight = sel.Value.Height;
        family.TemplateCount = sel.Value.Count;
        PresetFactory.RegenerateFamilySlots(family);
        SelectedProfile = family;
        OnPropertyChanged(nameof(SelectedFamilyResolution));
        OnPropertyChanged(nameof(SelectedFamilyCount));
        Save();
        return family;
    }

    public HotkeyBinding? SelectedHotkey
    {
        get => _selectedHotkey;
        set
        {
            if (SetProperty(ref _selectedHotkey, value))
            {
                CaptureHotkeyCommand.RaiseCanExecuteChanged();
                ClearHotkeyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ── Core operations ────────────────────────────────────────────────────────

    public void Refresh()
    {
        try
        {
            Windows.Clear();
            foreach (var window in _windowService.FindEveWindows(IncludeNotepadTestWindows))
                Windows.Add(window);

            Monitors.Clear();
            foreach (var monitor in _windowService.GetMonitors())
                Monitors.Add(monitor);

            if (string.IsNullOrWhiteSpace(LayoutTargetMonitorId) || Monitors.All(m => m.Id != LayoutTargetMonitorId))
                LayoutTargetMonitorId = Monitors.FirstOrDefault(m => m.IsPrimary)?.Id ?? Monitors.FirstOrDefault()?.Id ?? "";

            RebindRestartedWindows();
            DetectNewlyLaunchedClients();
            UpdateLiveSeatCharacters();
            LastUpdatedText = $"Last refresh {DateTime.Now:HH:mm:ss}";
            Status = $"Detected {Windows.Count} EVE/test windows and {Monitors.Count} monitors.";
            Log.Info(Status);
            OnPropertyChanged(nameof(WindowCount));
            OnPropertyChanged(nameof(MonitorCount));
            OnPropertyChanged(nameof(HasNoWindows));
            OnPropertyChanged(nameof(DetectionStateText));
            OnPropertyChanged(nameof(DetectionStateColor));
            RebuildMonitorPreview();

            // Live running-character labels changed with the detected windows above: refresh the
            // surfaces that snapshot them (they are not data-bound to DisplayLabel directly).
            RebuildMiniMap();
            RaiseIdentityDependents();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Log.Error($"Refresh failed: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            // Keep the active character set's stored seats in lockstep with the live Assignments before
            // serialising. All editing (rename, add/delete seat, per-seat font/colour) happens on the live
            // top-level Assignments; without this the set keeps a stale copy that resurfaces on a set
            // switch or reload -- the cause of labels/seats not persisting consistently.
            SnapshotLiveToActiveSet();
            _configService.Save(_settings);
            Status = $"Saved settings to {_configService.ConfigPath}.";
            Log.Info(Status);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Log.Error($"Save failed: {ex.Message}");
        }
    }

    public void Cleanup()
    {
        _frameTimer.Stop();
        _autoApplyTimer.Stop();
        _launchGroupCts?.Cancel();
        StopChatAlerts();
        StopCornerOverlays();
        StopTalkerOverlay();
        if (_frameOverlay is not null)
        {
            _frameOverlay.Close();
            _frameOverlay = null;
        }
    }

    // ── Settings backup ────────────────────────────────────────────────────────

    private SettingsBackup? _selectedBackup;

    public IReadOnlyList<SettingsBackup> AvailableBackups => _configService.GetBackups();

    public SettingsBackup? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                RestoreBackupCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(HasSelectedBackup));
            }
        }
    }

    public bool HasSelectedBackup => _selectedBackup is not null;

    public void RefreshBackups() => OnPropertyChanged(nameof(AvailableBackups));

    public void CreateBackupNow()
    {
        _configService.CreateBackup();
        OnPropertyChanged(nameof(AvailableBackups));
        Log.Info("Settings backup created.");
        Status = "Backup created.";
    }

    // Returns null on success, or an error message to show the user.
    public string? RestoreSelectedBackup()
    {
        if (_selectedBackup is null) return null;
        try
        {
            _configService.RestoreBackup(_selectedBackup.Path);
            Log.Info($"Restored settings from {_selectedBackup.DisplayName}. Restarting...");
            RestartApp();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Restore failed: {ex.Message}");
            return ex.Message;
        }
    }

    public void ExportSettings(string destPath)
    {
        try
        {
            File.Copy(_configService.ConfigPath, destPath, overwrite: true);
            Log.Info($"Settings exported to {destPath}.");
            Status = "Settings exported.";
        }
        catch (Exception ex) { Log.Error($"Export failed: {ex.Message}"); }
    }

    public void ImportSettings(string sourcePath)
    {
        try
        {
            // Validate JSON before overwriting.
            JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(sourcePath), new JsonSerializerOptions());
            _configService.CreateBackup(); // snapshot current before overwrite
            File.Copy(sourcePath, _configService.ConfigPath, overwrite: true);
            Log.Info($"Settings imported from {sourcePath}. Restarting...");
            RestartApp();
        }
        catch (Exception ex) { Log.Error($"Import failed: {ex.Message}"); }
    }

    private static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    private void RaiseCommandStates()
    {
        AssignSelectedCommand.RaiseCanExecuteChanged();
        AssignWindowToSlotCommand.RaiseCanExecuteChanged();
        RestoreSelectedStyleCommand.RaiseCanExecuteChanged();
        ToggleSelectedBorderlessCommand.RaiseCanExecuteChanged();
        DuplicateProfileCommand.RaiseCanExecuteChanged();
        DeleteProfileCommand.RaiseCanExecuteChanged();
        DeleteSelectedSlotCommand.RaiseCanExecuteChanged();
        ExportProfileCommand.RaiseCanExecuteChanged();
    }

    private void ScheduleAutoSave()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    // Write immediately if a debounced auto-save is pending -- e.g. when the window hides to the tray --
    // so a just-typed label isn't stuck in the 1s debounce if the process goes away before it fires.
    internal void FlushPendingSave()
    {
        if (_autoSaveTimer.IsEnabled) { _autoSaveTimer.Stop(); Save(); }
    }

    // ── Static helpers ─────────────────────────────────────────────────────────

    internal static uint ToHotkeyModifiers(ModifierKeys modifiers)
    {
        var flags = 0u;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= HotkeyDefaults.ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= HotkeyDefaults.ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= HotkeyDefaults.ModShift;
        return flags;
    }

    internal static string FormatGesture(uint modifiers, Key key)
    {
        var parts = new List<string>();
        if ((modifiers & HotkeyDefaults.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & HotkeyDefaults.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & HotkeyDefaults.ModShift) != 0) parts.Add("Shift");
        parts.Add(KeyToText(key));
        return string.Join("+", parts);
    }

    internal static string KeyToText(Key key) => key switch
    {
        Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
        Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
        Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
        _ => key.ToString()
    };

    internal static Brush ParseFrameBrush(string color)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(color);
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Orange;
        }
    }
}
