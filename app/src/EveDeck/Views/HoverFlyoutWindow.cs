using System.Windows;
using System.Windows.Interop;
using EveDeck.Utilities;

namespace EveDeck.Views;

// Temporary enlarged preview card shown when hovering a corner tile. Shows a DWM thumbnail
// of the corner client at ~50% master size without moving the actual EVE window.
// Positioned at the inner corner of the tile (the edge facing the master rect center).
internal sealed class HoverFlyoutWindow : Window
{
    private nint _thumbnailId;
    private readonly int _physX, _physY, _physW, _physH;

    public HoverFlyoutWindow(int physX, int physY, int physW, int physH, double dpiScale, nint sourceHwnd)
    {
        _physX = physX; _physY = physY; _physW = physW; _physH = physH;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        Left = physX / dpiScale;
        Top = physY / dpiScale;
        Width = physW / dpiScale;
        Height = physH / dpiScale;

        Loaded += (_, _) => OnLoaded(sourceHwnd);
    }

    private void OnLoaded(nint sourceHwnd)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow));

        // Physical pixel placement — always on top, never activates.
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost,
            _physX, _physY, _physW, _physH, Win32Native.SwpNoActivate);

        if (sourceHwnd != 0 && Win32Native.DwmRegisterThumbnail(hwnd, sourceHwnd, out _thumbnailId) == 0
            && Win32Native.GetClientRect(hwnd, out var cr))
        {
            var cw = cr.Right - cr.Left;
            var ch = cr.Bottom - cr.Top;
            if (cw > 0 && ch > 0)
            {
                var props = new Win32Native.DwmThumbnailProperties
                {
                    dwFlags = Win32Native.DwmTnpRectDestination | Win32Native.DwmTnpVisible,
                    rcDestination = new Win32Native.NativeRect { Left = 0, Top = 0, Right = cw, Bottom = ch },
                    fVisible = true
                };
                // Guarded like every other thumbnail update -- rcSource / DWM_TNP_RECTSOURCE would
                // show only part of the EVE client, which is against the EULA. See
                // SafetyGuard.ThrowIfSourceCrop and TileSurfaceWindow.ApplyThumbnailProperties.
                Services.SafetyGuard.ThrowIfSourceCrop(props.dwFlags);
                Win32Native.DwmUpdateThumbnailProperties(_thumbnailId, ref props);
            }
        }

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        return nint.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_thumbnailId != 0) { Win32Native.DwmUnregisterThumbnail(_thumbnailId); _thumbnailId = 0; }
    }
}
