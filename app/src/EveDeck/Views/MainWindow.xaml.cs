using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.ViewModels;

namespace EveDeck.Views;

public partial class MainWindow : Window
{
    private const double BaseWidth = 1200.0;
    private const double BaseHeight = 800.0;
    private const double BaseMinWidth = 980.0;
    private const double BaseMinHeight = 620.0;

    private readonly MainWindowViewModel _viewModel;
    private readonly HotkeyService _hotkeyService = new();
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private Point _windowDragStart;

    // evedeck:// URL commands forwarded from App (protocol pipe / startup args); routed through
    // the view-model's SafetyGuard-validated dispatcher.
    internal void HandleProtocolUrl(string url) => _viewModel.HandleProtocolUrl(url);

    // A native (Windows Action Center) notification was clicked -- see NativeNotificationService.
    // Every alert EveDeck still mirrors there (game events, PI) is informational, so clicking one
    // just brings EveDeck to the front. NativeNotificationService keeps its generic `argument`
    // channel for a future alert that needs to route somewhere specific.
    internal void HandleNativeNotificationActivated(string? payload) => ShowFromTray();

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _hotkeyService.HotkeyPressed += (_, binding) => _viewModel.HandleHotkey(binding.ActionId);
        _hotkeyService.ForegroundChanged += (_, hwnd) =>
        {
            _viewModel.RecordExternalForeground(hwnd, new WindowInteropHelper(this).Handle);
            _viewModel.ApplyProcessPriorities(hwnd);
            _viewModel.AutoMinimizeInactive(hwnd);
            _viewModel.RefreshTopmostForForeground(hwnd);
            // RefreshTopmostForForeground can freshly raise a pinned seat's window to HWND_TOPMOST,
            // in front of the overlay surfaces. Re-assert the surfaces (tile window, then its owned
            // label window) so the previews stay above pinned clients while EVE is focused.
            _viewModel.RefreshCornerOverlayZOrder();
        };
        _viewModel.HotkeysChanged += ViewModel_HotkeysChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        // A pending hotkey capture keeps ALL global hotkeys unregistered; abandon it if the user
        // clicks away to another app so the hotkeys come back (re-registered via IsCapturingHotkey).
        Deactivated += (_, _) => _viewModel.CancelHotkeyCapture();
        InitTrayIcon();
        SyncUiScaleComboBox(_viewModel.UiScale);
        ApplyUiScale(_viewModel.UiScale);
        RestoreWindowPosition();
        Loaded += MainWindow_Loaded;
    }

    private bool _setupShown;

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        MeasureTabStrip();

        // Show the first-run setup wizard once, after the main window is visible so it can own it.
        if (_setupShown || !_viewModel.NeedsSetup) return;
        _setupShown = true;
        ShowSetupWizard();
    }

    // Natural (unscaled) width of the whole tab strip, measured once the tabs are realized.
    private double _tabStripWidth;

    // The strip is hosted in a horizontal StackPanel, which never wraps -- so if the window is too
    // narrow the tabs clip off the edge instead. Measuring the strip lets ApplyMinWidth pin MinWidth
    // above it, making a too-narrow window unreachable in the first place.
    private void MeasureTabStrip()
    {
        double total = 0;
        foreach (var item in MainTabControl.Items)
        {
            if (item is not System.Windows.Controls.TabItem tab) continue;
            // A horizontal StackPanel measures children with unbounded width, so DesiredSize is already
            // the tab's natural width; only measure explicitly if layout hasn't reached it yet.
            if (tab.DesiredSize.Width <= 0)
                tab.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            total += tab.DesiredSize.Width;
        }

        if (total <= 0) return;
        _tabStripWidth = total;
        ApplyWindowMinimums();
    }

    // The minimums track UiScale: RootContent carries a ScaleTransform, so a strip that measures 1100
    // unscaled needs 1650 px of window at 1.5x. Fixed minimums (as these were in XAML) either clip the
    // tabs when scaled up, or stop the window shrinking to its own target Width when scaled down.
    private void ApplyWindowMinimums()
    {
        double scale = _viewModel.UiScale;
        // +10 covers RootContent's 1px border per side plus slack, so the last tab is never flush.
        double stripNeed = _tabStripWidth > 0 ? (_tabStripWidth * scale) + 10 : 0;
        MinWidth = Math.Max(BaseMinWidth * scale, stripNeed);
        MinHeight = BaseMinHeight * scale;
        if (Width < MinWidth) Width = MinWidth;
    }

    // Re-runnable from the Settings tab; on first run it's launched automatically.
    public void ShowSetupWizard()
    {
        var wizard = new SetupWizardWindow(_viewModel.Monitors) { Owner = this };
        var result = wizard.ShowDialog();
        if (result == true)
        {
            _viewModel.RunInitialSetup(wizard.ResultClientCount, wizard.ResultMonitorId, wizard.ResultFocusPreviewOnClick);
            MergeWizardSlots(wizard.ResultClientCount, wizard.ResultSlotAssignments);
            // The first character linked in the wizard becomes the app master (overrides the layout default).
            if (wizard.ResultMasterSeat > 0)
                _viewModel.SetMasterSeatNumber(wizard.ResultMasterSeat);
        }
        else
            _viewModel.DismissSetup();
    }

    private void MergeWizardSlots(int clientCount, IReadOnlyList<SlotAssignment> wizardSlots)
    {
        if (wizardSlots.All(s => s.EsiCharacters.Count == 0)) return;

        // Ensure slots exist for the chosen client count
        while (_viewModel.Assignments.Count < clientCount)
            _viewModel.Assignments.Add(new SlotAssignment { SlotNumber = _viewModel.Assignments.Count + 1, Label = $"Slot {_viewModel.Assignments.Count + 1}" });

        foreach (var wizardSlot in wizardSlots)
        {
            var actual = _viewModel.Assignments.FirstOrDefault(a => a.SlotNumber == wizardSlot.SlotNumber);
            if (actual is null) continue;
            foreach (var esiChar in wizardSlot.EsiCharacters)
            {
                if (_viewModel.Assignments.Any(a => a.EsiCharacters.Any(c => c.CharacterId == esiChar.CharacterId))) continue;
                actual.EsiCharacters.Add(esiChar);
                if (actual.EsiCharacters.Count == 1) actual.Label = esiChar.CharacterName;
                var title = $"EVE - {esiChar.CharacterName}";
                if (!actual.AssignedWindows.Any(w => w.Title.Equals(title, StringComparison.OrdinalIgnoreCase)))
                    actual.AssignedWindows.Add(new SlotWindowEntry { Title = title });
            }
        }
        _viewModel.Save();
    }

    private void RunSetupWizard_Click(object sender, RoutedEventArgs e) => ShowSetupWizard();

    private static readonly double[] UiScaleOptions = { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.UiScale))
        {
            SyncUiScaleComboBox(_viewModel.UiScale);
            ApplyUiScale(_viewModel.UiScale);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsCapturingHotkey))
        {
            // Unregister global hotkeys while capturing so the pressed combo reaches WPF
            // instead of being consumed by WM_HOTKEY and triggering the existing action.
            if (_viewModel.IsCapturingHotkey)
            {
                _hotkeyService.UnregisterAll(_viewModel.Log);
                // Ensure EveDeck window has keyboard focus so PreviewKeyDown fires when the user
                // presses their key combo (clicking the Set button can leave focus ambiguous).
                this.Activate();
                Keyboard.Focus(HotkeyDataGrid);
            }
            else
            {
                RegisterHotkeys();
                // Force the DataGrid to re-render so the updated Key Combo column is visible.
                HotkeyDataGrid.Items.Refresh();
            }
        }
    }

    private void SyncUiScaleComboBox(double scale)
    {
        var idx = Array.FindIndex(UiScaleOptions, v => Math.Abs(v - scale) < 0.001);
        if (idx >= 0 && UiScaleComboBox.SelectedIndex != idx)
            UiScaleComboBox.SelectedIndex = idx;
    }

    private void UiScaleComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (UiScaleComboBox.SelectedIndex >= 0 && UiScaleComboBox.SelectedIndex < UiScaleOptions.Length)
            _viewModel.UiScale = UiScaleOptions[UiScaleComboBox.SelectedIndex];
    }

    private void ApplyUiScale(double scale)
    {
        RootContent.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
        // Re-pin the floors for the new scale; also widens Width if the strip needs more than BaseWidth.
        ApplyWindowMinimums();
    }

    // Restore the last saved window position, but only if it still lands within the visible virtual
    // screen (guards against a monitor being unplugged since last run, which would orphan the window).
    private void RestoreWindowPosition()
    {
        if (_viewModel.WindowLeft is not { } left || _viewModel.WindowTop is not { } top) return;

        double vsLeft = SystemParameters.VirtualScreenLeft;
        double vsTop = SystemParameters.VirtualScreenTop;
        double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
        // Require the title bar to remain grabbable: at least a sliver of the top edge on-screen.
        if (left < vsLeft - Width + 80 || left > vsRight - 80 ||
            top < vsTop || top > vsBottom - 40) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    private void SaveWindowPosition()
    {
        // Only persist a normal (non-minimized/maximized) position; RestoreBounds gives the
        // last normal rect when the window is currently minimized or maximized.
        var rect = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (rect.IsEmpty) return;
        _viewModel.WindowLeft = rect.Left;
        _viewModel.WindowTop = rect.Top;
    }

    // 2a — System tray icon with context menu.
    private void InitTrayIcon()
    {
        // Always visible -- persistent tray presence regardless of window state (foreground,
        // minimized, or hidden), not just while minimized-to-tray.
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "EveDeck",
            Visible = true
        };

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add("Open EveDeck", null, (_, _) => ShowFromTray());
        contextMenu.Items.Add("Reload active profile", null, (_, _) => ReloadActiveProfileFromTray());
        _configProfilesMenu = new System.Windows.Forms.ToolStripMenuItem("Config profile");
        contextMenu.Items.Add(_configProfilesMenu);
        contextMenu.Items.Add("Check for Updates", null, (_, _) => _viewModel.CheckForUpdateCommand.Execute(null));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => ExitFromTray());
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();

        RebuildConfigProfilesMenu();
        _viewModel.ConfigProfilesChanged += (_, _) => Dispatcher.BeginInvoke(RebuildConfigProfilesMenu);
    }

    private System.Windows.Forms.ToolStripMenuItem? _configProfilesMenu;

    // The tray is the ONLY switcher for config profiles (the Options panel creates and edits them,
    // which is a different job). Rebuilt from scratch on every change rather than diffed -- the list
    // is a handful of items and a stale checkmark here is worse than the rebuild cost.
    private void RebuildConfigProfilesMenu()
    {
        if (_configProfilesMenu is null) return;
        _configProfilesMenu.DropDownItems.Clear();

        if (_viewModel.ConfigProfiles.Count == 0)
        {
            var empty = new System.Windows.Forms.ToolStripMenuItem("(none set up yet)") { Enabled = false };
            _configProfilesMenu.DropDownItems.Add(empty);
            return;
        }

        foreach (var profile in _viewModel.ConfigProfiles)
        {
            var captured = profile; // don't close over the loop variable's final value
            var item = new System.Windows.Forms.ToolStripMenuItem(captured.Name)
            {
                Checked = captured.Id == _viewModel.ActiveConfigProfileId,
            };
            item.Click += (_, _) => _viewModel.ApplyConfigProfileCommand.Execute(captured);
            _configProfilesMenu.DropDownItems.Add(item);
        }
    }

    // Load the bundled app icon (Assets\evedeck.ico, embedded as a WPF resource) for the tray, so the
    // notification-area icon matches the taskbar/exe icon instead of the generic Windows default.
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/evedeck.ico"));
            if (info?.Stream is { } stream)
                using (stream) return new System.Drawing.Icon(stream);
        }
        catch { /* fall back to the system icon if the resource can't be loaded */ }
        return System.Drawing.SystemIcons.Application;
    }

    // Re-apply the active layout profile without opening the window — handy after relaunching clients.
    private void ReloadActiveProfileFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            if (_viewModel.ApplyProfileCommand.CanExecute(null))
                _viewModel.ApplyProfileCommand.Execute(null);
        });
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // Set when the user genuinely wants to quit (tray › Exit), so OnClosing lets the close through
    // instead of bouncing the window back to the tray.
    private bool _exiting;

    private void ExitFromTray()
    {
        _exiting = true;
        Close();
    }

    // Closing the window (the X button) hides to tray rather than exiting, mirroring minimize — the
    // app keeps running so hotkeys and corner overlays stay live. Real exit comes from tray › Exit.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_exiting || !_viewModel.MinimizeToTray) return; // real close: OnClosed saves
        // Hiding to tray keeps the process alive, so flush any pending debounced save now in case the
        // machine shuts down / the process is killed before the 1s auto-save timer fires.
        _viewModel.FlushPendingSave();
        e.Cancel = true;
        HideToTray();
    }

    // 2a — Minimize to tray if the setting is enabled.
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray)
            HideToTray();
    }

    private void HideToTray() => Hide();

    private void ViewModel_HotkeysChanged(object? sender, EventArgs e) => RegisterHotkeys();

    // Leaving the Hotkeys tab with a capture still armed would strand every global hotkey
    // unregistered - abandon the capture instead.
    private void MainTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
            _viewModel.CancelHotkeyCapture();
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        RegisterHotkeys();
        HookTaskbarCreated();
    }

    // Windows broadcasts "TaskbarCreated" when the shell (Explorer) starts or restarts. On an Explorer
    // restart our tray icon is dropped; re-assert it so the right-click menu stays available.
    private int _taskbarCreatedMsg;
    private void HookTaskbarCreated()
    {
        _taskbarCreatedMsg = unchecked((int)EveDeck.Utilities.Win32Native.RegisterWindowMessage("TaskbarCreated"));
        if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(TrayWndProc);
    }

    private nint TrayWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0 && _notifyIcon is not null)
        {
            // Toggle Visible to force the icon to be re-added to the freshly created taskbar.
            _notifyIcon.Visible = false;
            _notifyIcon.Visible = true;
        }
        return 0;
    }

    private void RegisterHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var failures = _hotkeyService.RegisterAll(handle, _viewModel.Hotkeys, _viewModel.Log,
            _viewModel.RequireEveFocusForHotkeys, _viewModel.IsEveWindowForeground, _viewModel.HotkeysSuspended);
        _viewModel.SetHotkeyConflicts(failures);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_viewModel.TryCompleteHotkeyCapture(key, Keyboard.Modifiers))
            e.Handled = true;
    }

    // ── Drag-and-drop: window list → slot card ────────────────────────────────

    private void WindowsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _windowDragStart = e.GetPosition(null);
    }

    private void WindowsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _viewModel.SelectedWindow is null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _windowDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _windowDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        DragDrop.DoDragDrop(
            (DependencyObject)sender,
            new DataObject("EveWindowInfo", _viewModel.SelectedWindow),
            DragDropEffects.Link);
    }

    // The seat-card half of this now lives in SeatCardTemplate, which owns the seat card's markup and
    // handlers; the mini-map half stays here. A drag can cross both, so the mini-map drop path still
    // has to clear both.
    private void ClearAllDragIndicators()
    {
        SeatCardTemplate.ClearSeatDragIndicator();
        ClearMiniMapDragIndicator();
    }

    // Also used by SeatCardTemplate's drag auto-scroll.
    internal static T? FindVisualChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var found = FindVisualChild<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    // Keyboard equivalent of the grip drag, so reordering isn't mouse-only.
    private void SlotsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt) return;
        // Alt-chords route through Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Up && key != Key.Down) return;
        if (_viewModel.SelectedAssignment is not { } selected) return;

        var from = _viewModel.Assignments.IndexOf(selected);
        if (from < 0) return;
        var to = key == Key.Up ? from - 1 : from + 1;
        if (to < 0 || to >= _viewModel.Assignments.Count) return;

        _viewModel.Assignments.Move(from, to);
        _viewModel.Save();
        _viewModel.SelectedAssignment = selected;
        e.Handled = true;

        // Keep keyboard focus on the moved seat's container so repeated Alt+Up/Down keeps walking it.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SlotsListBox.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem container)
                container.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ── Minimap drag-drop (Clients tab) ──────────────────────────────────────

    private static bool MiniMapAccepts(DragEventArgs e)
        => e.Data.GetDataPresent("EveWindowInfo") || e.Data.GetDataPresent("SlotReorder");

    private MiniMapSlot? _currentDragOverCell;

    internal void ClearMiniMapDragIndicator()
    {
        if (_currentDragOverCell is not null)
        {
            _currentDragOverCell.IsDragOver = false;
            _currentDragOverCell = null;
        }
    }

    private void MiniMapSlot_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = MiniMapAccepts(e) ? DragDropEffects.Move : DragDropEffects.None;
        if (MiniMapAccepts(e) && sender is FrameworkElement { DataContext: MiniMapSlot cell })
        {
            if (_currentDragOverCell != cell)
            {
                ClearMiniMapDragIndicator();
                _currentDragOverCell = cell;
                cell.IsDragOver = true;
            }
        }
        e.Handled = true;
    }

    private void MiniMapSlot_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = MiniMapAccepts(e) ? DragDropEffects.Move : DragDropEffects.None;
        if (MiniMapAccepts(e) && sender is FrameworkElement { DataContext: MiniMapSlot cell })
        {
            if (_currentDragOverCell != cell)
            {
                ClearMiniMapDragIndicator();
                _currentDragOverCell = cell;
                cell.IsDragOver = true;
            }
        }
        else
        {
            ClearMiniMapDragIndicator();
        }
        e.Handled = true;
    }

    private void MiniMapSlot_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var pos = e.GetPosition(fe);
        if (pos.X < 0 || pos.Y < 0 || pos.X > fe.ActualWidth || pos.Y > fe.ActualHeight)
            ClearMiniMapDragIndicator();
    }

    private void MiniMapSlot_Drop(object sender, DragEventArgs e)
    {
        // Clear both indicators: a drag that crossed a seat card on its way to the mini-map can leave
        // that card's insert line set, and only ClearAllDragIndicators covers both.
        ClearAllDragIndicators();

        if (sender is not FrameworkElement { DataContext: MiniMapSlot miniSlot }) { e.Handled = true; return; }

        // Drag a seat card onto a cell → set its home corner (or master, on the center cell).
        if (e.Data.GetData("SlotReorder") is SlotAssignment seat)
        {
            _viewModel.SetSeatHomeCorner(seat.SlotNumber, miniSlot.SlotNumber);
            e.Handled = true;
            return;
        }

        // Drag a detected window onto a cell → assign it to the seat that currently occupies that cell.
        if (e.Data.GetData("EveWindowInfo") is not EveWindowInfo window) { e.Handled = true; return; }
        var assignment = _viewModel.Assignments.FirstOrDefault(a => a.SlotNumber == miniSlot.OccupantSeat);
        if (assignment is null) { e.Handled = true; return; }
        _viewModel.SelectedWindow = window;
        _viewModel.SelectedAssignment = assignment;
        _viewModel.AssignWindowToSlotCommand.Execute(assignment);
        e.Handled = true;
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => MainTabControl.SelectedItem = AboutTabItem;

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void FrameColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
            _viewModel.ActiveFrameColor = color;
    }

    private void FrameColorPick_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try
        {
            var current = (Color)System.Windows.Media.ColorConverter.ConvertFromString(_viewModel.ActiveFrameColor);
            dialog.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
        }
        catch { /* keep dialog's default color if the stored hex fails to parse */ }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var c = dialog.Color;
        _viewModel.ActiveFrameColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static string ColorToHex(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // Shows a WinForms font+color dialog seeded from the given family / WPF-DIP size / hex colour.
    // Returns the picked font on OK. WPF FontSize is in DIPs (1/96in); the dialog works in points
    // (1/72in), so convert on the way in and out (dip = pt * 96/72).
    internal static bool TryPickFont(string family, double sizeDip, string colorHex,
                             out string outFamily, out double outSizeDip, out string outColorHex)
    {
        outFamily = family; outSizeDip = sizeDip; outColorHex = colorHex;
        using var dialog = new System.Windows.Forms.FontDialog { ShowColor = true, ShowEffects = true, FontMustExist = true };
        var seedFamily = string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family;
        var pt = Math.Max(1f, (float)(sizeDip * 72.0 / 96.0));
        try { dialog.Font = new System.Drawing.Font(seedFamily, pt); }
        catch { try { dialog.Font = new System.Drawing.Font("Segoe UI", pt); } catch { /* dialog keeps its own default */ } }
        try
        {
            var wc = (Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
            dialog.Color = System.Drawing.Color.FromArgb(wc.R, wc.G, wc.B);
        }
        catch { /* keep the dialog default colour if the stored hex fails to parse */ }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;
        outFamily = dialog.Font.Name;
        outSizeDip = dialog.Font.SizeInPoints * 96.0 / 72.0;
        outColorHex = ColorToHex(dialog.Color);
        return true;
    }

    private void LabelFontPick_Click(object sender, RoutedEventArgs e)
    {
        var (family, sizeDip, color) = _viewModel.GlobalLabelFont();
        if (TryPickFont(family, sizeDip, color, out var f, out var s, out var c))
            _viewModel.ApplyGlobalLabelFont(f, s, c);
    }

    private void MasterLabelFontPick_Click(object sender, RoutedEventArgs e)
    {
        var (family, sizeDip, color) = _viewModel.GlobalMasterLabelFont();
        if (TryPickFont(family, sizeDip, color, out var f, out var s, out var c))
            _viewModel.ApplyGlobalMasterLabelFont(f, s, c);
    }

    private void MasterLabelFontReset_Click(object sender, RoutedEventArgs e)
        => _viewModel.ResetGlobalMasterLabelFont();

    private void MasterLabelStyleReset_Click(object sender, RoutedEventArgs e)
        => _viewModel.ResetGlobalMasterLabelStyle();

    // Combined "reset to normal" for the Options-page MASTER label section: clears both the font
    // override and the style/opacity overrides in one click (was two separate buttons before).
    private void MasterLabelStyleAndFontReset_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetGlobalMasterLabelFont();
        _viewModel.ResetGlobalMasterLabelStyle();
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var backup = _viewModel.SelectedBackup;
        if (backup is null) return;
        var result = System.Windows.MessageBox.Show(
            $"Restore settings from:\n{backup.DisplayName}\n\nEveDeck will restart. Current settings will be replaced.",
            "Restore Settings Backup",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.OK) return;
        var error = _viewModel.RestoreSelectedBackup();
        if (error is not null)
            System.Windows.MessageBox.Show(
                $"Restore failed:\n{error}",
                "Restore Failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CreateBackupNow();
        _viewModel.RefreshBackups();
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Settings",
            Filter = "JSON settings|*.json",
            FileName = $"evedeck_settings_{DateTime.Now:yyyy-MM-dd}.json"
        };
        if (dlg.ShowDialog(this) == true)
            _viewModel.ExportSettings(dlg.FileName);
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Settings",
            Filter = "JSON settings|*.json"
        };
        if (dlg.ShowDialog(this) == true)
            _viewModel.ImportSettings(dlg.FileName);
    }

    // Options-tab section search. Keyed by section index (matches the IndexToVisibility
    // ConverterParameter on each section StackPanel and the ListBoxItem order in OptionsMenu).
    // Built lazily on first search rather than at window construction, since the whole Options
    // tab subtree does not exist until the tab is first selected (TabControl's default template
    // only realizes the SelectedContent -- see App.xaml's TabControl ControlTemplate).
    private readonly Dictionary<int, string> _optionsSectionSearchText = new();
    private bool _optionsSearchIndexBuilt;

    private void OptionsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplyOptionsSearchFilter();

    // Sections whose content is data-driven (Config Profiles, Character Names) change while the app
    // runs, so a once-only index goes stale -- a profile added mid-session was previously unfindable
    // until restart. Invalidate whenever focus lands in the search box: you cannot add a profile or
    // rename a seat without taking focus away from here first, so this catches every realistic edit,
    // and it costs one rebuild per search session rather than one per keystroke.
    //
    // Deliberately NOT done by subscribing to ConfigProfiles/Assignments CollectionChanged:
    // ConfigProfiles is a pass-through to _settings.ConfigProfiles, and importing settings replaces
    // that collection wholesale, which would leave the subscription bound to a dead instance.
    private void OptionsSearchBox_GotFocus(object sender, RoutedEventArgs e)
        => _optionsSearchIndexBuilt = false;

    private void ApplyOptionsSearchFilter()
    {
        EnsureOptionsSearchIndexBuilt();

        var query = OptionsSearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            foreach (var obj in OptionsMenu.Items)
                if (obj is ListBoxItem item) item.Visibility = Visibility.Visible;
            OptionsMenu.Visibility = Visibility.Visible;
            OptionsNoMatchText.Visibility = Visibility.Collapsed;
            return;
        }

        int? firstMatch = null;
        var matchCount = 0;
        var selectedStillVisible = false;
        for (var i = 0; i < OptionsMenu.Items.Count; i++)
        {
            if (OptionsMenu.Items[i] is not ListBoxItem item) continue;
            var isMatch = _optionsSectionSearchText.TryGetValue(i, out var text)
                && text.Contains(query, StringComparison.OrdinalIgnoreCase);
            item.Visibility = isMatch ? Visibility.Visible : Visibility.Collapsed;
            if (!isMatch) continue;
            matchCount++;
            firstMatch ??= i;
            if (i == OptionsMenu.SelectedIndex) selectedStillVisible = true;
        }

        if (matchCount == 0)
        {
            OptionsMenu.Visibility = Visibility.Collapsed;
            OptionsNoMatchText.Visibility = Visibility.Visible;
            return;
        }

        OptionsMenu.Visibility = Visibility.Visible;
        OptionsNoMatchText.Visibility = Visibility.Collapsed;

        // If the currently displayed section fell out of the filtered set (or exactly one section
        // survives), jump to the first remaining match so the sidebar selection and the visible
        // right-hand panel never disagree.
        if (!selectedStillVisible && firstMatch.HasValue)
            OptionsMenu.SelectedIndex = firstMatch.Value;
    }

    private void EnsureOptionsSearchIndexBuilt()
    {
        if (_optionsSearchIndexBuilt) return;
        _optionsSearchIndexBuilt = true;
        // Rebuild from scratch rather than overwriting in place, so a section whose content shrank
        // cannot leave harvested text behind and keep matching a query it no longer contains.
        _optionsSectionSearchText.Clear();

        var originalIndex = OptionsMenu.SelectedIndex;
        for (var i = 0; i < OptionsSectionsHost.Children.Count; i++)
        {
            // Force each section to Visible (one at a time) and run a layout pass before harvesting
            // it. A Collapsed element's subtree is never measured in WPF, so an ItemsControl inside a
            // section that has never been the selected sidebar row (e.g. Config Profiles' row
            // template, Character Names' slot rows if a different section loads first) never gets its
            // ItemContainerGenerator to run and its DataTemplate content would be invisible to a plain
            // VisualTreeHelper walk. This whole loop runs synchronously with no dispatcher yield, so
            // WPF never composites an intermediate frame -- no visible flicker from the cycling.
            OptionsMenu.SelectedIndex = i;
            OptionsSectionsHost.UpdateLayout();

            if (OptionsSectionsHost.Children[i] is not FrameworkElement section) continue;
            var sb = new StringBuilder();
            if (i < OptionsMenu.Items.Count && OptionsMenu.Items[i] is ListBoxItem menuItem)
                sb.Append(menuItem.Content).Append(' ');
            HarvestOptionsSearchText(section, sb);
            _optionsSectionSearchText[i] = sb.ToString();
        }

        OptionsMenu.SelectedIndex = originalIndex;
        OptionsSectionsHost.UpdateLayout();
    }

    // Harvests user-visible strings from a section's visual subtree: TextBlock text, the Content of
    // CheckBox/RadioButton/Button, GroupBox/Expander headers, and ToolTip text. New settings become
    // searchable automatically as long as their label uses one of these standard controls -- no
    // hand-maintained keyword list to keep in sync.
    private static void HarvestOptionsSearchText(DependencyObject node, StringBuilder sb)
    {
        switch (node)
        {
            case TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text):
                sb.Append(tb.Text).Append(' ');
                break;
            case System.Windows.Controls.CheckBox cb when cb.Content is string cbText:
                sb.Append(cbText).Append(' ');
                break;
            case System.Windows.Controls.RadioButton rb when rb.Content is string rbText:
                sb.Append(rbText).Append(' ');
                break;
            case System.Windows.Controls.Button btn when btn.Content is string btnText:
                sb.Append(btnText).Append(' ');
                break;
            case System.Windows.Controls.GroupBox gb when gb.Header is string gbText:
                sb.Append(gbText).Append(' ');
                break;
            case Expander ex when ex.Header is string exText:
                sb.Append(exText).Append(' ');
                break;
        }

        if (node is FrameworkElement { ToolTip: string tipText })
            sb.Append(tipText).Append(' ');

        var childCount = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < childCount; i++)
            HarvestOptionsSearchText(VisualTreeHelper.GetChild(node, i), sb);
    }

    protected override void OnClosed(EventArgs e)
    {
        _notifyIcon?.Dispose();
        _viewModel.HotkeysChanged -= ViewModel_HotkeysChanged;
        _hotkeyService.UnregisterAll(_viewModel.Log);
        SaveWindowPosition();
        _viewModel.Cleanup();
        _viewModel.Save();
        base.OnClosed(e);
    }
}
