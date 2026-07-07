using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EveDeck.Models;
using EveDeck.Utilities;

namespace EveDeck.Views;

// A small, click-through, top-most name label shown at the top or bottom edge (or centre) of a tile
// (master or corner). Floats above the captured video -- which the DWM thumbnail / WGC swap-chain
// draw on top of WPF content -- so it lives in its own window rather than inside the tile.
//
// Appearance is chosen by AppSettings.CornerOverlayLabelStyle:
//   "Pill"     -- rounded name chip.
//   "IconText" -- character portrait + plain name with a soft shadow, no chip.
// Font family, size and text colour are supplied per label (resolved from the seat's overrides or the
// global default by the caller) and can be changed live via UpdateAppearance when occupancy swaps.
public partial class PillOverlay : Window
{
    private readonly double _dpiScale;
    private readonly bool _iconStyle;
    private readonly bool _centered;
    private readonly bool _atTop;
    private readonly int _baseHeight;
    private readonly int _physX, _physY, _physWidth, _physHeight;
    private double _pillHeightDip;
    private double _portraitDip;
    private double _fontSize;
    private string _portraitUrl = "";

    public PillOverlay(int physX, int physY, int physWidth, int physHeight,
                       double dpiScale, AppSettings settings, bool atTop, bool centered,
                       string fontFamily, double fontSize, string colorHex)
    {
        InitializeComponent();
        _dpiScale = dpiScale;
        _centered = centered;
        _atTop = atTop;
        _physX = physX;
        _physY = physY;
        _physWidth = physWidth;
        _physHeight = physHeight;
        _baseHeight = settings.CornerOverlayLabelHeight;
        _iconStyle = string.Equals(settings.CornerOverlayLabelStyle, "IconText", StringComparison.OrdinalIgnoreCase);

        // Style-fixed chrome (does not change per seat).
        if (_iconStyle)
        {
            // Soft shadow keeps a plain name legible over bright video.
            LabelText.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Colors.Black,
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.9
            };
            Pill.Background = System.Windows.Media.Brushes.Transparent;
            Pill.CornerRadius = new CornerRadius(0);
            Pill.Padding = new Thickness(6, 2, 6, 2);
        }
        else
        {
            // Round the edge that faces into the tile; a centred chip (corner tiles) is rounded all round.
            Pill.CornerRadius = centered
                ? new CornerRadius(6)
                : atTop ? new CornerRadius(0, 0, 6, 6) : new CornerRadius(6, 6, 0, 0);
        }

        ApplyAppearance(fontFamily, fontSize, colorHex);
        Place(physX, physY, physWidth, physHeight, atTop, centered);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Input-transparent + never activate, so it can't intercept clicks meant for the client.
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow | Win32Native.WsExTransparent));
        RePin();
    }

    // Font family, size and colour for this label. Style-fixed chrome (chip/shadow) stays as set in the
    // ctor; only text metrics change. Height/portrait scale with the font.
    private void ApplyAppearance(string family, double fontSize, string colorHex)
    {
        _fontSize = fontSize;
        LabelText.FontSize = fontSize;
        LabelText.FontFamily = string.IsNullOrWhiteSpace(family)
            ? new System.Windows.Media.FontFamily("Segoe UI")
            : new System.Windows.Media.FontFamily(family);
        LabelText.Foreground = BrushFromHex(colorHex, _iconStyle
            ? System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : System.Windows.Media.Color.FromRgb(0xE5, 0xE7, 0xEB));

        // Grow the label height with the font (and, for the icon style, the portrait) so large font
        // sizes are never clipped by the overlay window bounds.
        _portraitDip = _iconStyle ? fontSize + 8 : 0;
        var content = _iconStyle ? Math.Max(fontSize * 1.7, _portraitDip) : fontSize * 1.7;
        _pillHeightDip = Math.Max(_baseHeight, content) + 8;
    }

    // Live update on occupancy change: recolour/resize to the new occupant's font, re-pinning the
    // window (physical pixels) when the size -- and therefore the window height -- changed.
    public void UpdateAppearance(string family, double fontSize, string colorHex)
    {
        var sizeChanged = Math.Abs(fontSize - _fontSize) > 0.01;
        ApplyAppearance(family, fontSize, colorHex);
        if (_iconStyle) ApplyPortrait(); // resize the portrait to the new _portraitDip
        if (IsLoaded && sizeChanged) RePin();
    }

    public void SetText(string label) => SetContent(label, _portraitUrl);

    // Label plus an optional rounded character portrait (empty url hides the dot).
    public void SetContent(string label, string portraitUrl)
    {
        LabelText.Text = label;
        Visibility = string.IsNullOrWhiteSpace(label) ? Visibility.Hidden : Visibility.Visible;

        if (portraitUrl != _portraitUrl)
        {
            _portraitUrl = portraitUrl ?? "";
            ApplyPortrait();
        }
    }

    private void ApplyPortrait()
    {
        if (!_iconStyle || string.IsNullOrEmpty(_portraitUrl))
        {
            PortraitDot.Visibility = Visibility.Collapsed;
            PortraitDot.Width = PortraitDot.Height = 0;
            PortraitDot.Fill = null;
            return;
        }

        PortraitDot.Width = PortraitDot.Height = _portraitDip;
        PortraitDot.Visibility = Visibility.Visible;

        try
        {
            // Request a higher-resolution portrait (the seat url is the 64px variant) so the icon stays
            // sharp at large font sizes and high DPI.
            var url = _portraitUrl.Replace("size=64", "size=128");
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // Decode once at the actual on-screen size (physical px) instead of loading the full 128px
            // source and live-rescaling it into a ~20-30px icon on every render -- the latter is a
            // visible source of jitter for the icon specifically (the plain Pill style has no image and
            // doesn't show it).
            var physSize = Math.Max(1, (int)Math.Round(_portraitDip * _dpiScale));
            bmp.DecodePixelWidth = physSize;
            bmp.DecodePixelHeight = physSize;
            bmp.UriSource = new System.Uri(url);
            bmp.EndInit();

            // Keep whatever portrait is currently showing until the new one is actually ready -- a
            // refresh (occupancy swap, appearance update) should never blank the icon while the new
            // bitmap downloads, and a transient load failure shouldn't blank it either.
            if (bmp.IsDownloading)
                bmp.DownloadCompleted += (_, _) => PortraitDot.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            else
                PortraitDot.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        }
        catch { /* keep showing the previous portrait rather than blanking on a transient failure */ }
    }

    // Mirrors CornerOverlayWindow.RefreshZOrder so the label's z-order stays clamped to its tile's --
    // WPF's Topmost DP only clears WS_EX_TOPMOST, which reinserts the window at the TOP of the normal
    // band (not the bottom), so a label that was just topmost stays stranded above whatever the user
    // switches to (a browser, etc.) even after its tile has correctly sunk to HWND_BOTTOM.
    public void SetTopmost(bool value)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        const uint flags = Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate;
        if (value)
        {
            Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, 0, 0, 0, 0, flags);
        }
        else
        {
            Win32Native.SetWindowPos(hwnd, Win32Native.HwndNotTopmost, 0, 0, 0, 0, flags);
            Win32Native.SetWindowPos(hwnd, Win32Native.HwndBottom, 0, 0, 0, 0, flags);
        }
    }

    // Re-insert at the top of the topmost band so the name pill stays above its tile (which may itself
    // be raised to topmost while EVE is focused).
    public void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, 0, 0, 0, 0,
            Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate);
    }

    private static SolidColorBrush BrushFromHex(string hex, System.Windows.Media.Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(c);
            }
        }
        catch { /* fall through to the style default */ }
        return new SolidColorBrush(fallback);
    }

    // Physical-pixel rect for the label window: full tile width, height driven by the label, placed at
    // the tile centre / top / bottom to match the WPF Place() layout below.
    private (int x, int y, int w, int h) PhysicalPillRect()
    {
        var pillPhysH = (int)Math.Round(_pillHeightDip * _dpiScale);
        int y;
        if (_centered)
            y = (int)Math.Round(_physY + _physHeight / 2.0 - pillPhysH / 2.0);
        else if (_atTop)
            y = _physY;
        else
            y = _physY + _physHeight - pillPhysH;
        return (_physX, y, _physWidth, pillPhysH);
    }

    // Pin to the exact physical-pixel rect. WPF's DIP Left/Top place a top-level window through the
    // PRIMARY monitor's scale, which misplaces the label on a monitor with a different per-monitor DPI.
    // Positioning in physical pixels (as CornerOverlayWindow does for tiles) keeps the label aligned to
    // its tile at any scaling; content then renders at the target monitor's DPI, crisp.
    private void RePin()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        Height = _pillHeightDip;
        var (px, py, pw, ph) = PhysicalPillRect();
        Win32Native.SetWindowPos(hwnd, 0, px, py, pw, ph,
            Win32Native.SwpNoActivate | Win32Native.SwpNoZOrder);
    }

    // Initial DIP placement (approximate; OnLoaded/RePin re-pin to physical pixels once the HWND exists).
    public void Place(int physX, int physY, int physWidth, int physHeight, bool atTop, bool centered = false)
    {
        Left = physX / _dpiScale;
        Width = physWidth / _dpiScale;
        Height = _pillHeightDip;
        var topDip = physY / _dpiScale;
        Top = centered
            ? topDip + (physHeight / _dpiScale) / 2.0 - _pillHeightDip / 2.0
            : atTop ? topDip : topDip + (physHeight / _dpiScale) - _pillHeightDip;
    }
}
