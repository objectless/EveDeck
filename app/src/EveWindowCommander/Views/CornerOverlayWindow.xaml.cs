using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using EveWindowCommander.Models;
using EveWindowCommander.Services.Capture;
using EveWindowCommander.Utilities;

namespace EveWindowCommander.Views;

public partial class CornerOverlayWindow : Window
{
    // Optional log sink set once by the view-model so capture fallbacks surface in the Logs tab.
    public static Action<string>? Log;

    // Raised on a left-click anywhere on the tile. The view-model centres this tile's current
    // occupant (a focus switch — never input forwarded into the EVE client). See COMPLIANCE.md.
    public Action? Clicked;

    // Raised when the mouse enters/leaves this tile. Used for hover-to-peek.
    public Action? Hovered;
    public Action? HoverLeft;

    private const int WmLButtonDown = 0x0201;
    private bool _mouseTracking;

    private nint _thumbnailId;
    private readonly int _physX;
    private readonly int _physY;
    private readonly int _physWidth;
    private readonly int _physHeight;
    private readonly double _dpiScale;
    private readonly AppSettings _settings;

    // The tile's real on-screen client size in physical pixels, measured after the HWND exists.
    // Used for the DWM dest rect, the WGC swap-chain size, and the child surface — so the video fills
    // the tile exactly regardless of how WPF/HwndHost scaled things at the current DPI.
    private int _clientWidth;
    private int _clientHeight;

    // WGC path (high-quality capture). Falls back to DWM thumbnails if init fails or is unsupported.
    private bool _useWgc;
    private CaptureHwndHost? _captureHost;
    private WgcCornerCapture? _capture;
    private nint _pendingSource;

    public CornerOverlayWindow(int physX, int physY, int physWidth, int physHeight,
                                double dpiScale, AppSettings settings)
    {
        InitializeComponent();
        _physX = physX;
        _physY = physY;
        _physWidth = physWidth;
        _physHeight = physHeight;
        _clientWidth = physWidth;
        _clientHeight = physHeight;
        _dpiScale = dpiScale;
        _settings = settings;

        Left = physX / dpiScale;
        Top = physY / dpiScale;
        Width = physWidth / dpiScale;
        Height = physHeight / dpiScale;

        _useWgc = settings.CornerOverlayUseWgc && WinRtInterop.IsCaptureSupported();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW — don't steal focus, no taskbar/Alt-Tab entry.
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow));

        // Defer HwndHost creation to here so the parent HWND is fully styled before WPF subclasses
        // the child. Adding HwndHost in the constructor (before Show) caused WPF's HwndSubclass to
        // crash with DllNotFoundException inside a kernel window callback under WS_EX_TOOLWINDOW.
        if (_useWgc)
        {
            _captureHost = new CaptureHwndHost();
            CaptureHostContainer.Child = _captureHost;
        }

        // Pin the tile to its exact physical rect and drop it to the bottom. We position in physical
        // pixels via Win32 rather than trusting WPF's DIP Left/Top (which, with HwndHost children under
        // PerMonitorV2, can misplace/mis-size the tile at non-100% scaling — the cause of blank tiles).
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndBottom, _physX, _physY, _physWidth, _physHeight,
            Win32Native.SwpNoActivate);

        // Catch left-clicks at the Win32 level. This fires for both the DWM-thumbnail path (the click
        // lands on this window directly) and the WGC path (the STATIC child returns HTTRANSPARENT on
        // hit-test, so the click falls through to this parent window). WS_EX_NOACTIVATE means the click
        // is delivered without the tile stealing focus.
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProcHook);

        // Measure the real client size and pin the WGC child surface to it so the swap chain fills the tile.
        if (Win32Native.GetClientRect(hwnd, out var cr))
        {
            var cw = cr.Right - cr.Left;
            var ch = cr.Bottom - cr.Top;
            if (cw > 0 && ch > 0) { _clientWidth = cw; _clientHeight = ch; }
        }
        Log?.Invoke($"Corner tile at ({_physX},{_physY}) {_physWidth}x{_physHeight} phys; client {_clientWidth}x{_clientHeight}; dpiScale {_dpiScale:0.##}.");

        _captureHost?.ResizePhysical(_clientWidth, _clientHeight);

        // A source may have been set before the host HWND existed — start capture now.
        if (_useWgc && _pendingSource != 0) StartOrUpdateWgc(_pendingSource);
    }

    private nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmLButtonDown)
        {
            try { Clicked?.Invoke(); } catch { }
        }
        else if (msg == Win32Native.WmMouseMove)
        {
            // Always re-arm TME_LEAVE — a Z-order change (SetWindowPos HWND_BOTTOM) can silently
            // cancel TrackMouseEvent without sending WM_MOUSELEAVE, leaving _mouseTracking stuck true.
            var tme = new Win32Native.TrackMouseEventStruct
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32Native.TrackMouseEventStruct>(),
                dwFlags = Win32Native.TmeLeave,
                hwndTrack = hwnd,
                dwHoverTime = 0
            };
            Win32Native.TrackMouseEvent(ref tme);
            if (!_mouseTracking)
            {
                _mouseTracking = true;
                try { Hovered?.Invoke(); } catch { }
            }
        }
        else if (msg == Win32Native.WmMouseLeave)
        {
            _mouseTracking = false;
            try { HoverLeft?.Invoke(); } catch { }
        }
        return nint.Zero;
    }

    // Register (or re-register) the preview for the given source EVE window handle.
    public void UpdateSource(nint sourceHwnd)
    {
        _pendingSource = sourceHwnd;

        // A previously-lost source has reappeared (client relaunched) — make the tile visible again.
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;

        if (sourceHwnd == 0) return;

        if (_useWgc)
        {
            StartOrUpdateWgc(sourceHwnd);
            return;
        }

        RegisterDwmThumbnail(sourceHwnd);
    }

    // The source client closed (e.g. user mass-closed EVE). Hide the tile and release its capture
    // so a stale last frame isn't left frozen on screen. A later UpdateSource re-shows it.
    public void SourceLost()
    {
        _pendingSource = 0;
        if (Visibility == Visibility.Visible) Visibility = Visibility.Hidden;
        UnregisterThumbnail();
        try { _capture?.Dispose(); } catch { }
        _capture = null;
    }

    // ── WGC path ────────────────────────────────────────────────────────────────

    private void StartOrUpdateWgc(nint sourceHwnd)
    {
        if (_captureHost is null) { FallbackToDwm(sourceHwnd); return; }

        // Host HWND not realised yet — OnLoaded will retry with _pendingSource.
        if (_captureHost.Hwnd == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_useWgc && _pendingSource != 0) StartOrUpdateWgc(_pendingSource);
            }));
            return;
        }

        try
        {
            if (_capture is null)
            {
                _capture = new WgcCornerCapture(_clientWidth, _clientHeight);
                _capture.Start(sourceHwnd, _captureHost.Hwnd);
            }
            else
            {
                _capture.SetSource(sourceHwnd);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"WGC capture failed ({ex.Message}); using DWM thumbnail.");
            FallbackToDwm(sourceHwnd);
        }
    }

    private void FallbackToDwm(nint sourceHwnd)
    {
        _useWgc = false;
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        if (_captureHost is not null) CaptureHostContainer.Child = null;
        _captureHost = null;
        RegisterDwmThumbnail(sourceHwnd);
    }

    // ── DWM thumbnail path ──────────────────────────────────────────────────────

    private void RegisterDwmThumbnail(nint sourceHwnd)
    {
        UnregisterThumbnail();

        var destHwnd = new WindowInteropHelper(this).Handle;
        if (destHwnd == 0 || sourceHwnd == 0) return;

        if (Win32Native.DwmRegisterThumbnail(destHwnd, sourceHwnd, out _thumbnailId) != 0)
        {
            _thumbnailId = 0;
            return;
        }

        RefreshThumbnailRect();
    }

    // Recompute the thumbnail dest rect to fill the (16:9) tile.
    public void RefreshThumbnailRect()
    {
        if (_thumbnailId == 0) return;
        if (_clientWidth <= 0 || _clientHeight <= 0) return;

        var props = new Win32Native.DwmThumbnailProperties
        {
            dwFlags = Win32Native.DwmTnpRectDestination | Win32Native.DwmTnpVisible,
            rcDestination = new Win32Native.NativeRect
            {
                Left = 0,
                Top = 0,
                Right = _clientWidth,
                Bottom = _clientHeight
            },
            fVisible = true
        };
        Win32Native.DwmUpdateThumbnailProperties(_thumbnailId, ref props);
    }

    // Push this window back to HWND_BOTTOM if something else raised it.
    // Skipped while the mouse is over the tile: SetWindowPos can silently cancel TrackMouseEvent,
    // which would break hover-to-peek. The tile is only interesting to the user right now anyway.
    public void RefreshZOrder()
    {
        if (_mouseTracking) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndBottom, 0, 0, 0, 0,
            Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate);
    }

    private void UnregisterThumbnail()
    {
        if (_thumbnailId == 0) return;
        Win32Native.DwmUnregisterThumbnail(_thumbnailId);
        _thumbnailId = 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        UnregisterThumbnail();
        try { _capture?.Dispose(); } catch { }
        _capture = null;
    }
}
