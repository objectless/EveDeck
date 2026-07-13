using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using EveDeck.Utilities;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Rectangle = System.Windows.Shapes.Rectangle;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace EveDeck.Views;

// One always-on-top, click-through window that stacks short-lived toast notifications (chat
// keyword matches, non-combat game events) in the top-right corner of a monitor. Deliberately
// allowed to render OVER the corner-overlay preview tiles/master rect: during actual multiboxing
// the main EveDeck window is rarely on screen, so alerts need to be visible over the game itself.
// "Being shot at" combat alerts do NOT use this -- those get a tile-glow instead (see
// LabelSurfaceWindow.SetAlertGlow), since a toast per incoming hit would be constant spam.
internal sealed class ToastNotificationWindow : Window
{
    private const int ToastWidthPhys = 340;
    private const int MarginPhys = 20;
    private const int GapDip = 8;
    private const int MaxVisible = 6;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly StackPanel _stack = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly int _physX, _physY, _physWidth, _physHeight;
    private readonly double _toastWidthDip;

    public ToastNotificationWindow(int monitorPhysX, int monitorPhysY, int monitorPhysWidth, int monitorPhysHeight, double dpiScale)
    {
        _toastWidthDip = ToastWidthPhys / dpiScale;
        _physWidth = ToastWidthPhys;
        _physHeight = monitorPhysHeight - MarginPhys * 2;
        _physX = monitorPhysX + monitorPhysWidth - ToastWidthPhys - MarginPhys;
        _physY = monitorPhysY + MarginPhys;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        Left = _physX / dpiScale;
        Top = _physY / dpiScale;
        Width = _physWidth / dpiScale;
        Height = _physHeight / dpiScale;
        Content = _stack;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // Input-transparent + never activate, matching LabelSurfaceWindow -- toasts must never
        // intercept clicks meant for the tiles or the EVE clients underneath.
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow | Win32Native.WsExTransparent));

        // Pin to the exact physical rect (see LabelSurfaceWindow for why: WPF's DIP Left/Top place
        // a window through the PRIMARY monitor's scale, which misplaces it on a monitor running a
        // different per-monitor DPI).
        Win32Native.SetWindowPos(hwnd, 0, _physX, _physY, _physWidth, _physHeight,
            Win32Native.SwpNoActivate | Win32Native.SwpNoZOrder);
    }

    public nint Handle => new WindowInteropHelper(this).Handle;

    public void SetZ()
    {
        var hwnd = Handle;
        if (hwnd == 0) return;
        const uint flags = Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate;
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, 0, 0, 0, 0, flags);
    }

    // Queues one toast card at the top of the stack (newest first), fades it in, and auto-dismisses
    // it after Lifetime. accentHex colors the left edge bar -- callers pass the alert's rule/seat
    // color where available, else a neutral default.
    public void ShowToast(string title, string message, string accentHex)
    {
        if (_stack.Children.Count >= MaxVisible)
            _stack.Children.RemoveAt(_stack.Children.Count - 1);

        var accent = BrushFromHex(accentHex, Color.FromRgb(0x2B, 0xC0, 0xE4));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new Rectangle { Fill = accent, RadiusX = 2, RadiusY = 2 };
        Grid.SetColumn(bar, 0);

        var textPanel = new StackPanel { Margin = new Thickness(10, 8, 12, 8) };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xE4, 0xF0)),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(message))
        {
            textPanel.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x74, 0x88, 0xA0)),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        Grid.SetColumn(textPanel, 1);

        grid.Children.Add(bar);
        grid.Children.Add(textPanel);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x0F, 0x16, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x2C, 0x3A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, GapDip),
            Width = _toastWidthDip,
            Opacity = 0,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2, Opacity = 0.5 },
            Child = grid,
        };

        _stack.Children.Insert(0, card);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));

        var dismiss = new DispatcherTimer { Interval = Lifetime };
        dismiss.Tick += (_, _) =>
        {
            dismiss.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
            fadeOut.Completed += (_, _) => _stack.Children.Remove(card);
            card.BeginAnimation(OpacityProperty, fadeOut);
        };
        dismiss.Start();
    }

    private static SolidColorBrush BrushFromHex(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch { /* fall through to the default */ }
        return new SolidColorBrush(fallback);
    }
}
