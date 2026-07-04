using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using EveDeck.Utilities;

namespace EveDeck.Views;

public partial class ActiveFrameOverlay : Window
{
    public ActiveFrameOverlay() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyle,
            Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyle) | Win32Native.WsExTransparent);
    }

    public void ApplyFrame(int x, int y, int width, int height, int thickness, int glowRadius, Brush brush)
    {
        // The window is padded out beyond the client rect so the glow has room to bloom OUTWARD.
        // The rectangle is inset by `pad` (via Margin) so its stroke sits exactly on the client
        // edge; the blur then feathers into the surrounding pad region rather than inward.
        var blur = Math.Max(2.0, glowRadius);
        var pad = (int)Math.Ceiling(blur * 3) + thickness;
        FrameRect.Stroke = brush;
        FrameRect.StrokeThickness = thickness * 2;
        FrameRect.Margin = new Thickness(pad);
        FrameBlur.Radius = blur;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            Win32Native.SetWindowPos(hwnd, 0, x - pad, y - pad, width + pad * 2, height + pad * 2,
                Win32Native.SwpNoActivate | Win32Native.SwpNoZOrder | Win32Native.SwpShowWindow);
    }
}
