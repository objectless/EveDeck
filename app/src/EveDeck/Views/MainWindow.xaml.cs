using System.IO;
using System.Windows;
using System.Windows.Input;
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

    private readonly MainWindowViewModel _viewModel;
    private readonly HotkeyService _hotkeyService = new();
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private Point _windowDragStart;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _hotkeyService.HotkeyPressed += (_, binding) => _viewModel.HandleHotkey(binding.ActionId);
        _hotkeyService.ForegroundChanged += (_, hwnd) =>
        {
            _viewModel.ApplyProcessPriorities(hwnd);
            _viewModel.RefreshTopmostForForeground(hwnd);
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
        // Show the first-run setup wizard once, after the main window is visible so it can own it.
        if (_setupShown || !_viewModel.NeedsSetup) return;
        _setupShown = true;
        ShowSetupWizard();
    }

    // Re-runnable from the Settings tab; on first run it's launched automatically.
    public void ShowSetupWizard()
    {
        var wizard = new SetupWizardWindow(_viewModel.Monitors) { Owner = this };
        var result = wizard.ShowDialog();
        if (result == true)
        {
            _viewModel.RunInitialSetup(wizard.ResultClientCount, wizard.ResultMonitorId, wizard.ResultUseWgc, wizard.ResultFocusPreviewOnClick);
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
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => ExitFromTray());
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
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
        _hotkeyService.RegisterAll(handle, _viewModel.Hotkeys, _viewModel.Log,
            _viewModel.RequireEveFocusForHotkeys, _viewModel.IsEveWindowForeground);
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

    private SlotAssignment? _currentDragOverSlot;

    private void ClearAllDragIndicators()
    {
        if (_currentDragOverSlot is not null)
        {
            _currentDragOverSlot.IsDragSwapTarget = false;
            _currentDragOverSlot = null;
        }
    }

    private void SlotCard_DragOver(object sender, DragEventArgs e)
    {
        var valid = e.Data.GetDataPresent("EveWindowInfo") || e.Data.GetDataPresent("SlotReorder");
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;

        bool isReorder = e.Data.GetDataPresent("SlotReorder");
        if (valid && isReorder && sender is FrameworkElement { DataContext: SlotAssignment slot })
        {
            if (_currentDragOverSlot != slot)
            {
                ClearAllDragIndicators();
                _currentDragOverSlot = slot;
                slot.IsDragSwapTarget = true;
            }
        }
        else if (valid && !isReorder)
        {
            ClearAllDragIndicators();
        }
        e.Handled = true;
    }

    private void SlotCard_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = (e.Data.GetDataPresent("EveWindowInfo") || e.Data.GetDataPresent("SlotReorder"))
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SlotCard_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var pos = e.GetPosition(fe);
        if (pos.X < 0 || pos.Y < 0 || pos.X > fe.ActualWidth || pos.Y > fe.ActualHeight)
            ClearAllDragIndicators();
    }

    private void SlotCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotAssignment targetSlot }) { e.Handled = true; return; }

        ClearAllDragIndicators();

        if (e.Data.GetData("SlotReorder") is SlotAssignment draggedSlot)
        {
            var from = _viewModel.Assignments.IndexOf(draggedSlot);
            var to = _viewModel.Assignments.IndexOf(targetSlot);
            if (from >= 0 && to >= 0 && from != to)
            {
                // Swap: move dragged slot to target position, then slide the displaced target back.
                // After Move(from, to), the item originally at `to` shifts to to-1 (forward) or to+1 (backward).
                _viewModel.Assignments.Move(from, to);
                var targetNewIdx = from < to ? to - 1 : to + 1;
                _viewModel.Assignments.Move(targetNewIdx, from);
                _viewModel.Save();
            }
            e.Handled = true;
            return;
        }

        if (e.Data.GetData("EveWindowInfo") is not EveWindowInfo window) { e.Handled = true; return; }
        _viewModel.SelectedWindow = window;
        _viewModel.SelectedAssignment = targetSlot;
        _viewModel.AssignWindowToSlotCommand.Execute(targetSlot);
        e.Handled = true;
    }

    // ── Drag-and-drop: slot card grip → reorder ───────────────────────────────

    private Point _slotReorderDragStart;
    private SlotAssignment? _draggedSlotForReorder;

    private void SlotHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _slotReorderDragStart = e.GetPosition(null);
        if (sender is FrameworkElement { DataContext: SlotAssignment slot })
            _draggedSlotForReorder = slot;
    }

    private void SlotHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedSlotForReorder is null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _slotReorderDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _slotReorderDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var slot = _draggedSlotForReorder;
        _draggedSlotForReorder = null;
        DragDrop.DoDragDrop(
            (DependencyObject)sender,
            new DataObject("SlotReorder", slot),
            DragDropEffects.Move);
    }

    // ── Minimap drag-drop (Clients tab) ──────────────────────────────────────

    private static bool MiniMapAccepts(DragEventArgs e)
        => e.Data.GetDataPresent("EveWindowInfo") || e.Data.GetDataPresent("SlotReorder");

    private void MiniMapSlot_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = MiniMapAccepts(e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void MiniMapSlot_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = MiniMapAccepts(e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void MiniMapSlot_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MiniMapSlot miniSlot }) { e.Handled = true; return; }

        // Drag a seat card onto a cell → set its home corner (or master, on the centre cell).
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
    private bool TryPickFont(string family, double sizeDip, string colorHex,
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

    private void SlotLabelFontPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotAssignment seat }) return;
        var (family, sizeDip, color) = _viewModel.EffectiveSeatLabelFont(seat);
        if (TryPickFont(family, sizeDip, color, out var f, out var s, out var c))
            _viewModel.ApplySeatLabelFont(seat, f, s, c);
    }

    private void SlotLabelFontReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SlotAssignment seat })
            _viewModel.ApplySeatLabelFont(seat, null, null, null);
    }

    private void SlotFrameColorPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotAssignment seat }) return;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try
        {
            var current = (Color)System.Windows.Media.ColorConverter.ConvertFromString(_viewModel.EffectiveSeatFrameColor(seat));
            dialog.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
        }
        catch { /* keep the dialog default if the stored hex fails to parse */ }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var c = dialog.Color;
        _viewModel.ApplySeatFrameColor(seat, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    private void SlotFrameColorReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SlotAssignment seat })
            _viewModel.ApplySeatFrameColor(seat, null);
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
