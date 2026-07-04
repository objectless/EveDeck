using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EveDeck.Views;

// A minimal child HWND hosted in the WPF tree. The WGC swap chain presents into this window, so the
// custom DXGI rendering stays isolated from WPF's own composition (no airspace flicker) while the
// label strip is still drawn by WPF in the row beneath it.
internal sealed class CaptureHwndHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, bool repaint);

    public nint Hwnd { get; private set; }

    // Force the child surface to an exact physical-pixel rect. HwndHost auto-sizes the child from the
    // WPF layout via DPI conversion, which is unreliable under PerMonitorV2 at non-100% scaling — so we
    // pin it to the parent's real client size ourselves to guarantee the swap chain fills the tile.
    public void ResizePhysical(int width, int height)
    {
        if (Hwnd != 0 && width > 0 && height > 0)
            MoveWindow(Hwnd, 0, 0, width, height, true);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // "STATIC" is a always-registered system class; we only need a surface to present onto.
        Hwnd = CreateWindowExW(0, "STATIC", null, WsChild | WsVisible,
            0, 0, 0, 0, hwndParent.Handle, 0, 0, 0);
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        Hwnd = 0;
    }
}
