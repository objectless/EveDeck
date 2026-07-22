using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using EveDeck.Models;
using EveDeck.Utilities;
using EveDeck.ViewModels;

namespace EveDeck.Views;

// Code-behind for SeatCardTemplate.xaml. The seat card moved out of MainWindow.xaml verbatim, and a
// ResourceDictionary can only bind event handlers to its own x:Class, so its handlers came with it.
// Nothing here holds a MainWindow reference; everything is resolved from the sender's tree.
public partial class SeatCardTemplate : ResourceDictionary
{
    public SeatCardTemplate()
    {
        InitializeComponent();
    }

    // The seat card is only ever hosted by MainWindow, whose DataContext is the view-model. Popup
    // content lives in its own visual tree, where Window.GetWindow can come back null, so fall back
    // to the application's main window before giving up.
    private static MainWindowViewModel? ResolveViewModel(object sender)
    {
        if (sender is DependencyObject node && Window.GetWindow(node)?.DataContext is MainWindowViewModel fromTree)
            return fromTree;
        return System.Windows.Application.Current?.MainWindow?.DataContext as MainWindowViewModel;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    // Drag-and-drop: window list or seat card -> seat card.

    // Static because this dictionary is merged into Application.Resources exactly once, and
    // MainWindow's mini-map drop path has to clear this indicator too: a drag that crossed a seat
    // card on its way to the mini-map leaves that card's insert line set.
    private static SlotAssignment? _currentDragOverSlot;

    internal static void ClearSeatDragIndicator()
    {
        if (_currentDragOverSlot is not null)
        {
            _currentDragOverSlot.IsDragSwapTarget = false;
            _currentDragOverSlot = null;
        }
    }

    // Mirror of MainWindow.ClearAllDragIndicators from the seat-card side: a drag that crossed a
    // mini-map cell on its way to a seat card must not leave that cell highlighted.
    private static void ClearAllDragIndicators(object sender)
    {
        ClearSeatDragIndicator();
        if (sender is DependencyObject node && Window.GetWindow(node) is MainWindow mainWindow)
            mainWindow.ClearMiniMapDragIndicator();
    }

    // Drag auto-scroll (seat list): without this, a long seat list can't be reordered past the
    // visible viewport because nothing scrolls while the pointer is held down.

    private const double AutoScrollEdgeMargin = 28.0;
    private ScrollViewer? _slotsListScrollViewer;

    private ScrollViewer? FindSlotsListScrollViewer(object sender)
    {
        if (_slotsListScrollViewer is not null) return _slotsListScrollViewer;
        try
        {
            var list = FindAncestor<System.Windows.Controls.ListBox>(sender as DependencyObject);
            _slotsListScrollViewer = MainWindow.FindVisualChild<ScrollViewer>(list);
        }
        catch
        {
            _slotsListScrollViewer = null;
        }
        return _slotsListScrollViewer;
    }

    private void TryAutoScrollSlotsList(object sender, DragEventArgs e)
    {
        try
        {
            var scrollViewer = FindSlotsListScrollViewer(sender);
            if (scrollViewer is null) return;

            var pos = e.GetPosition(scrollViewer);
            if (pos.Y < AutoScrollEdgeMargin)
                scrollViewer.LineUp();
            else if (pos.Y > scrollViewer.ActualHeight - AutoScrollEdgeMargin)
                scrollViewer.LineDown();
        }
        catch
        {
            // Never let a drag auto-scroll failure interrupt the drag operation.
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
                ClearAllDragIndicators(sender);
                _currentDragOverSlot = slot;
                slot.IsDragSwapTarget = true;
            }
        }
        else if (valid && !isReorder)
        {
            ClearAllDragIndicators(sender);
        }

        if (valid) TryAutoScrollSlotsList(sender, e);

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
            ClearAllDragIndicators(sender);
    }

    private void SlotCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotAssignment targetSlot }) { e.Handled = true; return; }

        ClearAllDragIndicators(sender);

        if (ResolveViewModel(sender) is not { } viewModel) { e.Handled = true; return; }

        if (e.Data.GetData("SlotReorder") is SlotAssignment draggedSlot)
        {
            var from = viewModel.Assignments.IndexOf(draggedSlot);
            var to = viewModel.Assignments.IndexOf(targetSlot);
            if (from >= 0 && to >= 0 && from != to)
            {
                // Reorder (insert): drop the dragged seat at the target position; the seats in between
                // slide over by one. ObservableCollection.Move does exactly this in a single step -- a
                // proper drag-to-reorder, not a two-seat swap, so one drag lands the intended order.
                viewModel.Assignments.Move(from, to);
                viewModel.Save();
            }
            e.Handled = true;
            return;
        }

        if (e.Data.GetData("EveWindowInfo") is not EveWindowInfo window) { e.Handled = true; return; }
        viewModel.SelectedWindow = window;
        viewModel.SelectedAssignment = targetSlot;
        viewModel.AssignWindowToSlotCommand.Execute(targetSlot);
        e.Handled = true;
    }

    // Drag-and-drop: slot card grip -> reorder.

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

    // Per-seat label font / style / frame colour, all driven from the seat card's Style popup.

    private void SlotLabelFontPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotAssignment seat }) return;
        if (ResolveViewModel(sender) is not { } viewModel) return;
        var (family, sizeDip, color) = viewModel.EffectiveSeatLabelFont(seat);
        if (MainWindow.TryPickFont(family, sizeDip, color, out var f, out var s, out var c))
            viewModel.ApplySeatLabelFont(seat, f, s, c);
    }

    private void SlotMasterLabelFontPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotAssignment seat }) return;
        if (ResolveViewModel(sender) is not { } viewModel) return;
        var (family, sizeDip, color) = viewModel.EffectiveSeatLabelFont(seat, isMaster: true);
        if (MainWindow.TryPickFont(family, sizeDip, color, out var f, out var s, out var c))
            viewModel.ApplySeatMasterLabelFont(seat, f, s, c);
    }

    // Seeds a per-seat style ToggleButton's IsChecked from the seat's current effective value
    // (seat override ?? global) when its card first renders. Tag encodes which flag/tier: "Bold",
    // "Italic", "Shadow", "Outline" for the normal seat style, or the same names suffixed "Master".
    private void SlotStyleToggle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton { DataContext: SlotAssignment seat } tb
            && ResolveViewModel(sender) is { } viewModel)
            SeedStyleToggle(tb, seat, viewModel);
    }

    private static void SeedStyleToggle(System.Windows.Controls.Primitives.ToggleButton tb, SlotAssignment seat,
                                        MainWindowViewModel viewModel)
    {
        if (tb.Tag is not string tag) return;
        var isMaster = tag.EndsWith("Master", StringComparison.Ordinal);
        var flag = isMaster ? tag[..^"Master".Length] : tag;
        var (bold, italic, shadow, outline, _) = viewModel.EffectiveSeatLabelStyle(seat, isMaster);
        tb.IsChecked = flag switch
        {
            "Bold" => bold,
            "Italic" => italic,
            "Shadow" => shadow,
            "Outline" => outline,
            _ => false
        };
    }

    private void SlotStyleToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { DataContext: SlotAssignment seat } tb) return;
        if (tb.Tag is not string tag) return;
        if (ResolveViewModel(sender) is not { } viewModel) return;
        var isMaster = tag.EndsWith("Master", StringComparison.Ordinal);
        var flag = isMaster ? tag[..^"Master".Length] : tag;
        var value = tb.IsChecked ?? false;
        switch (flag)
        {
            case "Bold": viewModel.ApplySeatLabelBold(seat, isMaster, value); break;
            case "Italic": viewModel.ApplySeatLabelItalic(seat, isMaster, value); break;
            case "Shadow": viewModel.ApplySeatLabelDropShadow(seat, isMaster, value); break;
            case "Outline": viewModel.ApplySeatLabelOutline(seat, isMaster, value); break;
        }
    }

    // Toggles the per-seat style Popup open/closed. The Popup is named inside the same
    // DataTemplate instance as the button, so FrameworkElement.FindName resolves it via the
    // template's local NameScope (works per-seat-card, not just the first one).
    private void SlotStylePopup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.FindName("SeatStylePopup") is System.Windows.Controls.Primitives.Popup popup)
            popup.IsOpen = !popup.IsOpen;
    }

    // Seeds a per-seat opacity Slider from the seat's current effective value (mirrors
    // SeedStyleToggle). Tag: "Opacity" for the normal tier, "OpacityMaster" for the MASTER tier.
    // Guards ValueChanged during the programmatic seed so opening the popup doesn't write an
    // override for seats that don't have one yet.
    private bool _seedingSeatOpacitySlider;

    private void SlotOpacitySlider_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Slider { DataContext: SlotAssignment seat } slider
            && ResolveViewModel(sender) is { } viewModel)
            SeedOpacitySlider(slider, seat, viewModel);
    }

    private void SeedOpacitySlider(System.Windows.Controls.Slider slider, SlotAssignment seat,
                                   MainWindowViewModel viewModel)
    {
        if (slider.Tag is not string tag) return;
        var isMaster = tag.EndsWith("Master", StringComparison.Ordinal);
        var (_, _, _, _, opacity) = viewModel.EffectiveSeatLabelStyle(seat, isMaster);
        _seedingSeatOpacitySlider = true;
        slider.Value = opacity;
        _seedingSeatOpacitySlider = false;
    }

    private void SlotOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seedingSeatOpacitySlider) return;
        if (sender is not System.Windows.Controls.Slider { DataContext: SlotAssignment seat } slider) return;
        if (slider.Tag is not string tag) return;
        if (ResolveViewModel(sender) is not { } viewModel) return;
        var isMaster = tag.EndsWith("Master", StringComparison.Ordinal);
        viewModel.ApplySeatLabelOpacity(seat, isMaster, (int)Math.Round(e.NewValue));
    }

    // Resets one seat's font + style + opacity overrides for one tier (normal or MASTER) in a
    // single click, then re-seeds every control in the same row so the popup reflects the
    // now-inherited values immediately (Loaded only fires once).
    private void SlotLabelStyleReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotAssignment seat } el) return;
        if (ResolveViewModel(sender) is not { } viewModel) return;
        viewModel.ApplySeatLabelFont(seat, null, null, null);
        viewModel.ResetSeatLabelStyle(seat, isMaster: false);
        ReseedSeatStyleToggles(el, seat, viewModel);
    }

    private void SlotMasterLabelStyleReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotAssignment seat } el) return;
        if (ResolveViewModel(sender) is not { } viewModel) return;
        viewModel.ApplySeatMasterLabelFont(seat, null, null, null);
        viewModel.ResetSeatLabelStyle(seat, isMaster: true);
        ReseedSeatStyleToggles(el, seat, viewModel);
    }

    private void ReseedSeatStyleToggles(FrameworkElement resetButton, SlotAssignment seat, MainWindowViewModel viewModel)
    {
        if (resetButton.Parent is not System.Windows.Controls.Panel panel) return;
        foreach (var child in panel.Children)
        {
            if (child is System.Windows.Controls.Primitives.ToggleButton tb && tb.Tag is string)
                SeedStyleToggle(tb, seat, viewModel);
            else if (child is System.Windows.Controls.Slider slider && slider.Tag is string)
                SeedOpacitySlider(slider, seat, viewModel);
        }
    }

    private void SlotFrameColorPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotAssignment seat }) return;
        if (ResolveViewModel(sender) is not { } viewModel) return;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try
        {
            var current = (Color)System.Windows.Media.ColorConverter.ConvertFromString(viewModel.EffectiveSeatFrameColor(seat));
            dialog.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
        }
        catch { /* keep the dialog default if the stored hex fails to parse */ }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var c = dialog.Color;
        viewModel.ApplySeatFrameColor(seat, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    private void SlotFrameColorReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SlotAssignment seat } && ResolveViewModel(sender) is { } viewModel)
            viewModel.ApplySeatFrameColor(seat, null);
    }

    // Reads the clipboard for a copied EVE "Edit Theme" string and applies Primary/Accent to this
    // seat's frame/label colours. See Utilities.EveThemeString for the expected format.
    private void SlotPasteTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotAssignment seat }) return;
        if (ResolveViewModel(sender) is not { } viewModel) return;

        string clip;
        try { clip = System.Windows.Clipboard.GetText(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not read the clipboard: {ex.Message}",
                "Paste Theme", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!EveThemeString.TryParse(clip, out var primary, out var accent))
        {
            System.Windows.MessageBox.Show(
                "Clipboard doesn't look like an EVE theme string. Use the copy button in EVE's " +
                "Edit Theme dialog first (expects \"#RRGGBB,#RRGGBB,...\").",
                "Paste Theme", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        viewModel.ApplySeatTheme(seat, primary, accent);
    }
}
