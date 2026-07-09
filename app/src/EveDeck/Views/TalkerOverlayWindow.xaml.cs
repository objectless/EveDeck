using System.Windows;
using EveDeck.Models;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace EveDeck.Views;

// EveDeck-rendered "who is talking" panel fed by the Mumble plugin bridge (see
// Services/MumbleBridgeService). Unlike UtilityOverlayChrome this window owns its content --
// there is no foreign window to wrap, so it's a plain themed WPF window: drag anywhere to move,
// lock toggle to pin, auto-sizes to its roster. Position persists in AppSettings.TalkerOverlay
// (X/Y in WPF DIPs -- this is our own window, not a Win32-managed foreign rect).
public partial class TalkerOverlayWindow : Window
{
    private UtilityOverlaySlot? _slot;
    private Action? _onPersist;

    public TalkerOverlayWindow()
    {
        InitializeComponent();
    }

    public void Bind(UtilityOverlaySlot slot, Action onPersist)
    {
        _slot = slot;
        _onPersist = onPersist;
        LockToggle.IsChecked = slot.Locked;
        // 0,0 means never positioned -- land somewhere visible instead of the screen corner.
        (Left, Top) = slot is { X: 0, Y: 0 } ? (120d, 120d) : (slot.X, slot.Y);
        ApplyOpacity();
    }

    public void ApplyOpacity()
        => Opacity = Math.Clamp(_slot?.OpacityPercent ?? 100, 20, 100) / 100.0;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_slot is null || _slot.Locked) return;
        // DragMove blocks until the button is released, so persisting right after it is safe.
        DragMove();
        _slot.X = (int)Left;
        _slot.Y = (int)Top;
        _onPersist?.Invoke();
    }

    private void LockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_slot is null) return;
        _slot.Locked = LockToggle.IsChecked == true;
        _onPersist?.Invoke();
    }
}
