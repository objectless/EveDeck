using System.Windows;
using System.Windows.Interop;
using EveDeck.Models;
using EveDeck.Utilities;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace EveDeck.Views;

// EveDeck-rendered "who is talking" panel fed by the Mumble plugin bridge (see
// Services/MumbleBridgeService). Unlike UtilityOverlayChrome this window owns its content --
// there is no foreign window to wrap, so it's a plain themed WPF window: drag anywhere to move,
// lock toggle to pin, auto-sizes to its roster. Position persists in AppSettings.TalkerOverlay
// (X/Y in WPF DIPs -- this is our own window, not a Win32-managed foreign rect).
public partial class TalkerOverlayWindow : Window
{
    private const double MinScalePercent = 60;
    private const double MaxScalePercent = 300;

    private UtilityOverlaySlot? _slot;
    private Action? _onPersist;
    private bool _resizing;
    private Point _resizeStartCursor;
    private double _resizeStartScale;

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
        var scale = Math.Clamp(slot.ScalePercent, MinScalePercent, MaxScalePercent) / 100.0;
        ContentScale.ScaleX = scale;
        ContentScale.ScaleY = scale;
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

    // Dragging the grip scales the whole panel uniformly (LayoutTransform, so SizeToContent
    // resizes the window to match) instead of stretching literal width/height -- the panel's
    // content reflows with its roster, so growing bare whitespace wouldn't make text more
    // readable the way a bigger scale does.
    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_slot is null || _slot.Locked) return;
        _resizing = true;
        _resizeStartCursor = PointToScreen(e.GetPosition(this));
        _resizeStartScale = ContentScale.ScaleX * 100.0;
        MouseMove += ResizeGrip_MouseMove;
        MouseLeftButtonUp += ResizeGrip_MouseLeftButtonUp;
        ((System.Windows.UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void ResizeGrip_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_resizing || _slot is null) return;
        var cur = PointToScreen(e.GetPosition(this));
        // Diagonal drag distance (down-right grows, up-left shrinks), not raw X or Y alone, so
        // the grip behaves like a normal corner-drag regardless of which axis moves more.
        var delta = (cur.X - _resizeStartCursor.X + (cur.Y - _resizeStartCursor.Y)) / 2.0;
        var newScale = Math.Clamp(_resizeStartScale + delta, MinScalePercent, MaxScalePercent);
        ContentScale.ScaleX = newScale / 100.0;
        ContentScale.ScaleY = newScale / 100.0;
    }

    private void ResizeGrip_MouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        MouseMove -= ResizeGrip_MouseMove;
        MouseLeftButtonUp -= ResizeGrip_MouseLeftButtonUp;
        System.Windows.Input.Mouse.Capture(null);
        if (_slot is not null)
        {
            _slot.ScalePercent = (int)Math.Round(ContentScale.ScaleX * 100.0);
            _onPersist?.Invoke();
        }
    }

    // Re-raise to the top of the topmost band without moving/resizing. Pinned EVE clients and the
    // corner tile/label surfaces re-assert HWND_TOPMOST on their own tick (see ActiveFrameOverlay),
    // which can shove this window back behind them since Windows only tracks insertion order within
    // the topmost band, not a fixed z-order -- previously this needed a manual disable/re-enable
    // toggle to fix. Called from the same 1s timer that refreshes the roster so it self-heals.
    public void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, 0, 0, 0, 0,
                Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate);
    }
}
