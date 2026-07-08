using System.Windows;
using System.Windows.Interop;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Drawing.Point;

namespace EveDeck.Views;

// A small chrome window that lets the user drag/resize/lock a REAL external window (Mumble's
// Talking UI) that EveDeck doesn't own. The target is positioned INSET inside this window's edges (a
// top drag-strip + a thin border margin) so the two never share pixels -- no click-through/hit-
// test tricks needed, unlike ActiveFrameOverlay (which is purely decorative and click-through).
// The target is kept z-ordered just above this window so it visually occludes the interior,
// leaving only the strip/border visible around it. See Utilities.EveThemeString for an unrelated
// but similarly-scoped "reuse the real app, don't reimplement it" precedent (theme paste feature).
public partial class UtilityOverlayChrome : Window
{
    private const int StripHeightDip = 26;
    // Wide enough to actually grab with a mouse -- at 8 DIP the edge-resize zones were nearly
    // impossible to hit (the target window occludes everything inside the margin, so the visible
    // margin IS the entire hit area; it cannot be padded inward).
    private const int BorderMarginDip = 14;
    private const int MinWidthDip = 200;
    private const int MinHeightDip = 150;

    private UtilityOverlaySlot? _slot;
    private Win32WindowService? _windowService;
    private Action? _onPersist;

    [Flags]
    private enum ResizeEdges { None = 0, Left = 1, Right = 2, Bottom = 4 }

    private bool _dragging;
    private ResizeEdges _resizeEdges;
    private Point _dragStartCursor;
    private WindowRect _dragStartRect = new();
    private int _appliedOpacityPercent = -1;

    public nint TargetHandle { get; private set; }

    public event Action? DetachRequested;

    public UtilityOverlayChrome(string title)
    {
        InitializeComponent();
        TitleText.Text = title;
    }

    // Live per-call DPI lookup for wherever the target currently sits, rather than a value cached
    // once at chrome creation -- that cached-at-creation approach was wrong whenever the chrome's
    // native window happened to be created (e.g. via EnsureHandle in Reposition) while still
    // sitting at its off-screen XAML default position, on a different monitor/DPI than the target.
    private double CurrentDpiScale(int x, int y) => _windowService?.GetDpiScaleForPoint(x, y) ?? 1.0;

    // Binds this chrome to a live target window and (re)draws it at the slot's saved position.
    public void Attach(nint targetHandle, UtilityOverlaySlot slot, Win32WindowService windowService, Action onPersist)
    {
        TargetHandle = targetHandle;
        _slot = slot;
        _windowService = windowService;
        _onPersist = onPersist;
        LockToggle.IsChecked = slot.Locked;
        _appliedOpacityPercent = -1; // target may be a fresh window (Mumble restarted) -- re-apply
        Reposition();
        ApplyOpacity();
    }

    // Re-applies the slot's opacity to the target and this chrome. Cheap early-out when the value
    // hasn't changed, so the VM can call this freely (e.g. on every slider tick) and Reposition's
    // per-second cadence doesn't spam SetWindowLongPtr (a logged, dangerous API).
    public void ApplyOpacity()
    {
        if (_slot is null || _windowService is null || TargetHandle == 0) return;
        var pct = Math.Clamp(_slot.OpacityPercent, 20, 100);
        if (pct == _appliedOpacityPercent) return;
        _appliedOpacityPercent = pct;

        // Target: Win32 layered alpha (it's a foreign window). Chrome: WPF's own Opacity --
        // SetLayeredWindowAttributes doesn't reliably stick on a WPF-rendered window, which left
        // the strip/border fully opaque while the target faded. AllowsTransparency is set in XAML
        // so the Opacity property is actually honored.
        if (pct >= 100)
            _windowService.RemoveOpacity(TargetHandle);
        else
            _windowService.SetWindowOpacity(TargetHandle, pct);
        Opacity = pct / 100.0;
    }

    public bool IsTargetAlive() => _windowService?.IsWindowAlive(TargetHandle) ?? false;

    // A minimized window's GetWindowRect is a Windows placeholder, never a real screen position --
    // and Mumble's own Talking UI can park itself off-screen at a small size when its speaker
    // roster is empty (if "always show" isn't enabled in Mumble's own settings). No real monitor
    // layout in this app's supported range extends anywhere near -10000, so the coordinate alone is
    // a safe signal regardless of the window's size (mirrors MainWindowViewModel.UtilityOverlays.IsLikelySentinelRect).
    private static bool IsLikelySentinelRect(WindowRect rect) => rect.X < -10000 || rect.Y < -10000;

    // Re-applies the slot's current X/Y/Width/Height to both the real target window and this
    // chrome's own frame. Called after Attach and on every drag/resize tick.
    public void Reposition()
    {
        if (_slot is null || _windowService is null || TargetHandle == 0) return;

        var targetRect = new WindowRect { X = _slot.X, Y = _slot.Y, Width = _slot.Width, Height = _slot.Height };
        if (IsLikelySentinelRect(targetRect))
        {
            // The saved slot rect is itself a sentinel -- most likely persisted while the target was
            // parked off-screen. Blindly re-forcing that same bad rect on every tick (as this method
            // used to) would keep it stuck there forever, even once the target moves somewhere real.
            // Re-derive from the target's live rect instead; if that's ALSO a sentinel right now
            // (e.g. Mumble's roster is still empty), there's nothing good to adopt yet, so skip this
            // tick entirely rather than reinforcing the bad position.
            if (!_windowService.TryGetWindowRect(TargetHandle, out var live) || IsLikelySentinelRect(live))
                return;
            (_slot.X, _slot.Y, _slot.Width, _slot.Height) = (live.X, live.Y, live.Width, live.Height);
            _onPersist?.Invoke();
            targetRect = live;
        }
        _windowService.MoveResizeWindow(TargetHandle, targetRect);

        // Some target apps don't honor an externally requested size exactly -- GTK2 Pidgin in
        // particular can clamp back up to its own internal minimum content size instead of
        // shrinking to whatever we asked for. Re-query the target's actual resulting rect and wrap
        // the chrome around THAT instead of blindly trusting the request, or the chrome's frame and
        // the real window visibly diverge (the resize grip ends up occluded by the mismatched
        // target, making it look broken/impossible to grab).
        var actualRect = _windowService.TryGetWindowRect(TargetHandle, out var queried) ? queried : targetRect;

        // EnsureHandle (not the plain .Handle getter) because Attach() calls Reposition() before
        // Show() -- on that first call the chrome's native HWND doesn't exist yet, .Handle would
        // return IntPtr.Zero, the old "if (hwnd != 0)" guard would skip positioning entirely, and
        // Show() would then display the chrome at its XAML-default Left="-10000" Top="-10000"
        // (off-screen) instead of over the target. EnsureHandle forces creation now.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();

        var dpiScale = CurrentDpiScale(actualRect.X, actualRect.Y);
        var stripPhys = (int)Math.Round(StripHeightDip * dpiScale);
        var borderPhys = (int)Math.Round(BorderMarginDip * dpiScale);

        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost,
            actualRect.X - borderPhys, actualRect.Y - stripPhys,
            actualRect.Width + borderPhys * 2, actualRect.Height + borderPhys + stripPhys,
            Win32Native.SwpNoActivate | Win32Native.SwpShowWindow);

        // Re-assert AFTER the chrome so the target ends up front-most in the topmost band and
        // visually occludes the chrome's backdrop in their overlapping region.
        _windowService.SetWindowTopmost(TargetHandle, true);
    }

    private void DragStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_slot is null || _slot.Locked) return;
        BeginTrack(ResizeEdges.None);
        e.Handled = true;
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_slot is null || _slot.Locked) return;
        BeginTrack(ResizeEdges.Right | ResizeEdges.Bottom);
        e.Handled = true;
    }

    // The backdrop border's only visible (and therefore only clickable) region is the thin margin
    // around the target window, so any click landing on it is by construction near an edge --
    // classify it into left/right/bottom (+ corner combos) and start a resize from that edge.
    // Clicks in the interior never reach us; the target window sits above and swallows them.
    private void BackdropBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_slot is null || _slot.Locked) return;
        var edges = EdgesAt(e.GetPosition(this));
        if (edges == ResizeEdges.None) return;
        BeginTrack(edges);
        e.Handled = true;
    }

    // Live cursor feedback while hovering the border margin (not during an active track).
    private void BackdropBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || _resizeEdges != ResizeEdges.None) return;
        if (sender is not FrameworkElement el) return;
        if (_slot is null || _slot.Locked) { el.Cursor = null; return; }
        el.Cursor = EdgesAt(e.GetPosition(this)) switch
        {
            ResizeEdges.Left or ResizeEdges.Right => System.Windows.Input.Cursors.SizeWE,
            ResizeEdges.Bottom => System.Windows.Input.Cursors.SizeNS,
            ResizeEdges.Left | ResizeEdges.Bottom => System.Windows.Input.Cursors.SizeNESW,
            ResizeEdges.Right | ResizeEdges.Bottom => System.Windows.Input.Cursors.SizeNWSE,
            _ => null,
        };
    }

    // Corner zones are deliberately larger than the border margin itself so diagonal grabs don't
    // require pixel-perfect aim. Positions are in DIPs relative to this chrome window.
    private ResizeEdges EdgesAt(System.Windows.Point pos)
    {
        const double cornerZone = 28;
        var edges = ResizeEdges.None;
        if (pos.X <= (pos.Y >= ActualHeight - cornerZone ? cornerZone : BorderMarginDip + 2)) edges |= ResizeEdges.Left;
        else if (pos.X >= ActualWidth - (pos.Y >= ActualHeight - cornerZone ? cornerZone : BorderMarginDip + 2)) edges |= ResizeEdges.Right;
        if (pos.Y >= ActualHeight - (edges != ResizeEdges.None ? cornerZone : BorderMarginDip + 2)) edges |= ResizeEdges.Bottom;
        return edges;
    }

    private void BeginTrack(ResizeEdges edges)
    {
        _resizeEdges = edges;
        _dragging = edges == ResizeEdges.None;
        _dragStartCursor = System.Windows.Forms.Cursor.Position;
        // Base the drag delta on the target's ACTUAL current rect, not the last-requested _slot
        // values -- if the target didn't honor a previous resize request exactly (see Reposition),
        // those would diverge and the first bit of drag movement would visually "jump" to reconcile
        // the mismatch instead of tracking the cursor 1:1 from where things really are.
        _dragStartRect = _windowService is not null && _windowService.TryGetWindowRect(TargetHandle, out var rect)
            ? rect
            : new WindowRect { X = _slot!.X, Y = _slot.Y, Width = _slot.Width, Height = _slot.Height };
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_slot is null || (!_dragging && _resizeEdges == ResizeEdges.None)) return;

        var cur = System.Windows.Forms.Cursor.Position;
        var dx = cur.X - _dragStartCursor.X;
        var dy = cur.Y - _dragStartCursor.Y;

        if (_dragging)
        {
            _slot.X = _dragStartRect.X + dx;
            _slot.Y = _dragStartRect.Y + dy;
        }
        else
        {
            var dpiScale = CurrentDpiScale(_dragStartRect.X, _dragStartRect.Y);
            var minW = (int)Math.Round(MinWidthDip * dpiScale);
            var minH = (int)Math.Round(MinHeightDip * dpiScale);

            if (_resizeEdges.HasFlag(ResizeEdges.Right))
                _slot.Width = Math.Max(minW, _dragStartRect.Width + dx);
            else if (_resizeEdges.HasFlag(ResizeEdges.Left))
            {
                // Keep the right edge anchored: clamp width first, then derive X from it.
                var w = Math.Max(minW, _dragStartRect.Width - dx);
                _slot.X = _dragStartRect.X + _dragStartRect.Width - w;
                _slot.Width = w;
            }
            if (_resizeEdges.HasFlag(ResizeEdges.Bottom))
                _slot.Height = Math.Max(minH, _dragStartRect.Height + dy);
        }

        Reposition();
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging && _resizeEdges == ResizeEdges.None) return;
        _dragging = false;
        _resizeEdges = ResizeEdges.None;
        ReleaseMouseCapture();
        _onPersist?.Invoke();
    }

    private void LockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_slot is null) return;
        _slot.Locked = LockToggle.IsChecked == true;
        _onPersist?.Invoke();
    }

    private void DetachButton_Click(object sender, RoutedEventArgs e) => DetachRequested?.Invoke();
}
