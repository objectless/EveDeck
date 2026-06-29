using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EveWindowCommander.Models;
using EveWindowCommander.Utilities;

namespace EveWindowCommander.Views;

// A small, click-through, top-most name pill shown at the top or bottom edge of a tile (master or
// corner). Floats above the captured video — which the DWM thumbnail / WGC swap-chain draw on top of
// WPF content — so it lives in its own window rather than inside the tile.
public partial class PillOverlay : Window
{
    private readonly double _dpiScale;
    private readonly double _pillHeightDip;
    private readonly double _portraitDip;
    private string _portraitUrl = "";

    public PillOverlay(int physX, int physY, int physWidth, int physHeight,
                       double dpiScale, AppSettings settings, bool atTop)
    {
        InitializeComponent();
        _dpiScale = dpiScale;
        _pillHeightDip = settings.CornerOverlayLabelHeight + 8;
        _portraitDip = settings.CornerOverlayLabelFontSize + 6;

        LabelText.FontSize = settings.CornerOverlayLabelFontSize;
        // Round the edge that faces into the tile so the pill reads as tucked against the border.
        Pill.CornerRadius = atTop ? new CornerRadius(0, 0, 6, 6) : new CornerRadius(6, 6, 0, 0);

        Place(physX, physY, physWidth, physHeight, atTop);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Input-transparent + never activate, so it can't intercept clicks meant for the client.
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow | Win32Native.WsExTransparent));
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
        if (string.IsNullOrEmpty(_portraitUrl))
        {
            PortraitDot.Visibility = Visibility.Collapsed;
            PortraitDot.Width = PortraitDot.Height = 0;
            PortraitDot.Fill = null;
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new System.Uri(_portraitUrl);
            bmp.EndInit();
            PortraitDot.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            PortraitDot.Width = PortraitDot.Height = _portraitDip;
            PortraitDot.Visibility = Visibility.Visible;
        }
        catch
        {
            PortraitDot.Visibility = Visibility.Collapsed;
        }
    }

    public void SetTopmost(bool value) { if (Topmost != value) Topmost = value; }

    public void Place(int physX, int physY, int physWidth, int physHeight, bool atTop)
    {
        Left = physX / _dpiScale;
        Width = physWidth / _dpiScale;
        Height = _pillHeightDip;
        var topDip = physY / _dpiScale;
        Top = atTop ? topDip : topDip + (physHeight / _dpiScale) - _pillHeightDip;
    }
}
