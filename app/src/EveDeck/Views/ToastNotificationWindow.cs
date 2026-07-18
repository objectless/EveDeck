using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
using Cursors = System.Windows.Input.Cursors;
using Ellipse = System.Windows.Shapes.Ellipse;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace EveDeck.Views;

// One row of a multi-line toast. Primary carries the thing that happened ("Nanites extractor expires
// in 2h"); Secondary the context it happened to ("Ostingele V (Barren)"). Splitting them lets the
// card render a readable two-tone row per alert instead of one wall of joined text.
internal readonly record struct ToastLine(string Primary, string? Secondary);

// A titled cluster of rows -- PI groups its alerts under the owning character, so the name is stated
// once as a header instead of being repeated at the front of every row.
internal readonly record struct ToastGroup(string Header, IReadOnlyList<ToastLine> Lines);

// Corner/edge the toast stack anchors to, parsed from AppSettings.ToastPosition. Bottom* anchors the
// stack's BOTTOM edge and grows upward as cards stack up (newest nearest the anchored edge, matching
// Windows' own notification behavior near the system clock); Top* anchors the top edge and grows
// downward (the original, only, behavior before this setting existed).
internal enum ToastAnchor { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

// One always-on-top window that stacks short-lived Discord-style toast notifications (chat keyword
// matches, non-combat game events, PI alerts, bundled combat alerts) in the top-right corner of a
// monitor. Deliberately allowed to render OVER the corner-overlay preview tiles/master rect: during
// actual multiboxing the main EveDeck window is rarely on screen, so alerts need to be visible over
// the game itself. "Being shot at" combat alerts also get a tile-glow (LabelSurfaceWindow.SetAlertGlow)
// for real-time "which tile" feedback; the toast is the persistent record.
//
// Input model: the cards are CLICKABLE (click focuses the alert's seat, like clicking a preview
// tile), so this window does NOT set WS_EX_TRANSPARENT. To keep it from swallowing clicks meant for
// EVE, the window is resized to exactly bound its cards (see UpdateBounds) instead of spanning the
// monitor -- so when no toast is showing, there is no window in the way at all. WS_EX_NOACTIVATE
// stays set: clicking a toast must never pull focus away from the EVE client.
internal sealed class ToastNotificationWindow : Window
{
    private const int ToastWidthPhys = 380;
    private const int MarginPhys = 20;
    private const int GapDip = 10;
    private const int AvatarDip = 40;
    private const int MaxVisible = 6;

    // A bundled alert (a PI refresh can raise a dozen colonies at once) must not grow into a column
    // running the whole height of the monitor: past this the card's body scrolls instead of growing.
    private const double BodyMaxHeightDip = 190;

    // Ceiling for the whole stack, as a fraction of the monitor -- belt and braces with
    // BodyMaxHeightDip above, which bounds any single card.
    private const double MaxStackFractionOfMonitor = 0.6;

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    // Discord's dark-theme notification palette.
    private static readonly Color CardBg = Color.FromRgb(0x1E, 0x1F, 0x22);
    private static readonly Color CardBgHover = Color.FromRgb(0x2B, 0x2D, 0x31);
    private static readonly Color CardBorder = Color.FromRgb(0x2E, 0x30, 0x35);
    private static readonly Color TitleFg = Color.FromRgb(0xF2, 0xF3, 0xF5);
    private static readonly Color BodyFg = Color.FromRgb(0xB5, 0xBA, 0xC1);

    private readonly StackPanel _stack = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly int _physX, _physWidth, _physMaxHeight;
    private readonly int _physTopFixed; // Top* anchors: fixed top edge, stack grows downward
    private readonly int _physBottomFixed; // Bottom* anchors: fixed bottom edge, stack grows upward
    private readonly bool _growUpward;
    private readonly double _dpiScale;
    private readonly double _toastWidthDip;

    // `monitorWork*` should be the monitor's WORK AREA (excludes the taskbar), not its full bounds --
    // that's what makes BottomRight land just above the system clock instead of behind the taskbar.
    public ToastNotificationWindow(int monitorWorkX, int monitorWorkY, int monitorWorkWidth, int monitorWorkHeight, double dpiScale, ToastAnchor anchor)
    {
        _dpiScale = dpiScale;
        _toastWidthDip = ToastWidthPhys / dpiScale;
        _physWidth = ToastWidthPhys;
        _physMaxHeight = (int)((monitorWorkHeight - MarginPhys * 2) * MaxStackFractionOfMonitor);
        _growUpward = anchor is ToastAnchor.BottomLeft or ToastAnchor.BottomCenter or ToastAnchor.BottomRight;

        _physX = anchor switch
        {
            ToastAnchor.TopLeft or ToastAnchor.BottomLeft => monitorWorkX + MarginPhys,
            ToastAnchor.TopCenter or ToastAnchor.BottomCenter => monitorWorkX + (monitorWorkWidth - ToastWidthPhys) / 2,
            _ => monitorWorkX + monitorWorkWidth - ToastWidthPhys - MarginPhys,
        };
        _physTopFixed = monitorWorkY + MarginPhys;
        _physBottomFixed = monitorWorkY + monitorWorkHeight - MarginPhys;

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
        Top = TopForHeight(0) / dpiScale;
        Width = _physWidth / dpiScale;
        Height = 0;
        Content = _stack;
    }

    // Top* anchors keep a fixed top edge; Bottom* anchors keep a fixed BOTTOM edge, so the top edge
    // has to move up as the stack grows taller.
    private int TopForHeight(int height) => _growUpward ? _physBottomFixed - height : _physTopFixed;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // NOACTIVATE: a toast must never steal foreground from the EVE client the user is playing --
        // the window still receives mouse clicks, it just never activates. TOOLWINDOW keeps it out of
        // alt-tab. Deliberately NOT WS_EX_TRANSPARENT (unlike LabelSurfaceWindow) -- the cards are
        // clickable; UpdateBounds keeps the window itself off everything except the cards.
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow));

        // Deliberately does NOT call UpdateBounds() here. This fires mid-way through the external
        // .Show() call (before EnsureToastWindow's caller has added the first card), so calling
        // UpdateBounds() with zero children would set Visibility=Hidden WHILE WPF's own Show() is
        // still completing -- a re-entrant assignment that raced .Show()'s own Visibility bookkeeping
        // badly enough that the SUBSEQUENT Visibility=Visible from the first real AddCard became a
        // silent no-op (WPF skips the DP callback when the new value already matches its cached one).
        // Net effect, confirmed live: the very first toast of a session was created with a real HWND
        // at the right position/size, but IsWindowVisible was false -- invisible, not merely delayed.
        // The window starts at Height=0 from the constructor either way, so there is nothing useful
        // for this handler to size/show yet; AddCard's own UpdateBounds() call (after .Show() has
        // fully returned) is the only place this window's Visibility should ever be touched.
    }

    public nint Handle => new WindowInteropHelper(this).Handle;

    // True while at least one card is on screen. Callers gate z-order re-assertion on this so the
    // toast only fights for the topmost slot during the few seconds it's actually visible, rather
    // than churning forever (that unconditional-reassert pattern is the historical source of every
    // "overlay flicker" bug in this codebase -- see MaintainCornerOverlays).
    public bool HasVisibleToasts => _stack.Children.Count > 0;

    public void SetZ()
    {
        var hwnd = Handle;
        if (hwnd == 0) return;
        const uint flags = Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate;
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, 0, 0, 0, 0, flags);
    }

    // Pins the window to the exact physical rect its cards need. Physical pixels rather than WPF's
    // DIP Left/Top for the same reason as LabelSurfaceWindow: DIP placement goes through the PRIMARY
    // monitor's scale, which misplaces the window on a monitor running a different per-monitor DPI.
    // Height tracks the content so the window never covers anything but its own cards -- with no
    // toasts up it collapses to nothing and can't intercept a click at all.
    private void UpdateBounds()
    {
        var hwnd = Handle;
        if (hwnd == 0) return;

        // Hidden rather than merely zero-height when empty: a zero-height window still measures ~2px
        // on screen, and since this window is topmost and NOT click-through that leaves a 380x2 strip
        // across the top-right corner able to swallow a click (exactly where a maximized window's
        // close button lives). Hidden windows take no input at all.
        if (_stack.Children.Count == 0)
        {
            Visibility = Visibility.Hidden;
            return;
        }

        _stack.Measure(new System.Windows.Size(_toastWidthDip, double.PositiveInfinity));
        var neededPhys = (int)Math.Ceiling(_stack.DesiredSize.Height * _dpiScale);
        var height = Math.Min(neededPhys, _physMaxHeight);
        var top = TopForHeight(height);

        Height = height / _dpiScale;
        Win32Native.SetWindowPos(hwnd, 0, _physX, top, _physWidth, height,
            Win32Native.SwpNoActivate | Win32Native.SwpNoZOrder);
        Visibility = Visibility.Visible;
    }

    // Queues one toast card at the top of the stack (newest first), slides it in from the right, and
    // auto-dismisses it after Lifetime. `avatar` is the seat's cached character portrait where one is
    // known; without it the card falls back to an accent-colored circle carrying the title's initial.
    // `onClick` (when given) fires on click and dismisses the card immediately.
    public void ShowToast(string title, string message, string accentHex, ImageSource? avatar = null, Action? onClick = null)
    {
        FrameworkElement? body = string.IsNullOrWhiteSpace(message)
            ? null
            : new TextBlock
            {
                Text = message,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(BodyFg),
                TextWrapping = TextWrapping.Wrap,
            };
        AddCard(title, body, accentHex, avatar, onClick);
    }

    // Multi-line variant: one readable row per alert (dot + primary + muted secondary) rather than a
    // block of newline-joined text. Used by bundled alerts where every line shares one context.
    public void ShowToast(string title, IReadOnlyList<ToastLine> lines, string accentHex, ImageSource? avatar = null, Action? onClick = null)
    {
        var accent = BrushFromHex(accentHex, Color.FromRgb(0x2B, 0xC0, 0xE4));
        var rows = new StackPanel();
        foreach (var line in lines)
            rows.Children.Add(BuildRow(line, accent));
        AddCard(title, rows, accentHex, avatar, onClick, VerticalAlignment.Top);
    }

    // Grouped variant: alerts clustered under a header (PI groups per character, so the name is a
    // header stated once instead of repeated on every row). Each group is the character name, then
    // its planet rows indented beneath it.
    public void ShowToast(string title, IReadOnlyList<ToastGroup> groups, string accentHex, ImageSource? avatar = null, Action? onClick = null)
    {
        var accent = BrushFromHex(accentHex, Color.FromRgb(0x2B, 0xC0, 0xE4));
        var body = new StackPanel();

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            body.Children.Add(new TextBlock
            {
                Text = group.Header,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TitleFg),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, i == 0 ? 0 : 12, 0, 0),
            });

            var groupRows = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
            foreach (var line in group.Lines)
                groupRows.Children.Add(BuildRow(line, accent));
            body.Children.Add(groupRows);
        }

        AddCard(title, body, accentHex, avatar, onClick, VerticalAlignment.Top);
    }

    // Intel-report variant: `title` stays "SYSTEM -- N jumps away" (unchanged from the plain string
    // overload), but the body leads with an icon row for what the report actually said, when the
    // intel line had anything past the bare system name to go on. For a Sighting, `primaryDetail`/
    // `secondaryDetail` are typically a pilot name and the ship they're in (see
    // IntelSystemTokenizer.ResolvePilotAndShip) -- `secondaryDetail` null means only one of the two
    // was resolved (or ship-name recognition wasn't available), so `primaryDetail` alone is shown,
    // identical to this method's original single-detail behavior. `shipIcon`, when given (a resolved
    // ship whose icon is already cached -- see ShipIconCacheService), replaces the plain accent dot
    // with the actual ship's icon. NoVisual/Clear ignore both `shipIcon` and the details in favor of
    // a fixed label. Every detail null/empty renders with no icon row at all -- purely additive over
    // a bare "system was mentioned" line.
    public void ShowIntelToast(string title, IntelReportKind kind, string? primaryDetail, string? secondaryDetail, string rawMessage, string accentHex, ImageSource? shipIcon = null, Action? onClick = null)
    {
        var accent = BrushFromHex(accentHex, Color.FromRgb(0x8B, 0x5C, 0xF6));
        var body = new StackPanel();

        var label = kind switch
        {
            IntelReportKind.NoVisual => "No visual",
            IntelReportKind.Clear => "Clear",
            _ => primaryDetail ?? "",
        };
        var secondary = kind == IntelReportKind.Sighting ? secondaryDetail : null;
        if (label.Length > 0)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            FrameworkElement icon = kind switch
            {
                IntelReportKind.NoVisual => BuildNoVisualIcon(),
                IntelReportKind.Clear => BuildClearIcon(),
                IntelReportKind.Sighting when shipIcon is not null => BuildShipIcon(shipIcon),
                _ => new Ellipse { Width = 8, Height = 8, Fill = accent, Margin = new Thickness(0, 5, 0, 0) },
            };
            Grid.SetColumn(icon, 0);

            // Reuses BuildRow's exact two-tier layout (primary + muted secondary line beneath it) when
            // a ship AND a pilot both resolved, instead of the single-line label used everywhere else
            // in this method -- same visual language already established for PI's bundled alert rows.
            FrameworkElement labelElement = !string.IsNullOrWhiteSpace(secondary)
                ? BuildRow(new ToastLine(label, secondary), accent, includeDot: false)
                : new TextBlock
                {
                    Text = label,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(kind == IntelReportKind.Clear ? Color.FromRgb(0x4A, 0xDE, 0x80) : TitleFg),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            labelElement.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(labelElement, 1);

            row.Children.Add(icon);
            row.Children.Add(labelElement);
            body.Children.Add(row);
        }

        body.Children.Add(new TextBlock
        {
            Text = rawMessage,
            FontSize = 12,
            Foreground = new SolidColorBrush(BodyFg),
            TextWrapping = TextWrapping.Wrap,
        });

        AddCard(title, body, accentHex, null, onClick, VerticalAlignment.Top);
    }

    // Eye outline + pupil, struck through -- "no visual": the system was named but nobody's actually
    // laid eyes on what's in it (spotted on d-scan/local mention only). Drawn as plain vector shapes
    // rather than an icon-font glyph so it renders identically regardless of what fonts are installed.
    private static FrameworkElement BuildNoVisualIcon()
    {
        var red = new SolidColorBrush(Color.FromRgb(0xF2, 0x3F, 0x42));
        var canvas = new Canvas { Width = 16, Height = 16, Margin = new Thickness(0, 2, 0, 0) };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M1,8 C3,3 13,3 15,8 C13,13 3,13 1,8 Z"),
            Stroke = red,
            StrokeThickness = 1.4,
        });
        var pupil = new Ellipse { Width = 4, Height = 4, Fill = red };
        Canvas.SetLeft(pupil, 6);
        Canvas.SetTop(pupil, 6);
        canvas.Children.Add(pupil);
        canvas.Children.Add(new Line { X1 = 0, Y1 = 15, X2 = 16, Y2 = 1, Stroke = red, StrokeThickness = 1.6 });
        return canvas;
    }

    // A resolved ship's actual icon (ESI images CDN, pre-cached by ShipIconCacheService) in a small
    // rounded frame -- same visual slot as the plain accent dot/crossed-eye/checkmark above it.
    private static FrameworkElement BuildShipIcon(ImageSource icon) => new Border
    {
        Width = 18,
        Height = 18,
        CornerRadius = new CornerRadius(3),
        ClipToBounds = true,
        Child = new System.Windows.Controls.Image { Source = icon, Stretch = Stretch.UniformToFill },
    };

    // Simple checkmark -- a previously-reported system has been called clear.
    private static FrameworkElement BuildClearIcon()
    {
        var green = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
        var canvas = new Canvas { Width = 16, Height = 16, Margin = new Thickness(0, 2, 0, 0) };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,8 L6,12 L14,3"),
            Stroke = green,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        });
        return canvas;
    }

    // One alert row: accent dot + primary (white) over an optional muted secondary line.
    // `includeDot` suppresses the leading accent dot for callers that already draw their own leading
    // icon in an outer layout (e.g. ShowIntelToast's crossed-eye/checkmark row) -- true everywhere
    // else, unchanged from this method's original single-purpose behavior.
    private static FrameworkElement BuildRow(ToastLine line, SolidColorBrush accent, bool includeDot = true)
    {
        var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (includeDot)
        {
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = accent,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 8, 0),
            };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);
        }

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = line.Primary,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(TitleFg),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(line.Secondary))
        {
            text.Children.Add(new TextBlock
            {
                Text = line.Secondary,
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 0),
                Foreground = new SolidColorBrush(BodyFg),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        Grid.SetColumn(text, 1);

        row.Children.Add(text);
        return row;
    }

    private void AddCard(string title, FrameworkElement? body, string accentHex, ImageSource? avatar, Action? onClick,
                         VerticalAlignment avatarAlign = VerticalAlignment.Center)
    {
        // Oldest card is evicted at the cap. Which end is "oldest" depends on stack direction: a
        // top-anchored stack inserts newest at index 0 (oldest trails at the end); a bottom-anchored
        // stack appends newest at the end instead (oldest leads at index 0) so the newest card lands
        // nearest the anchored edge, matching Windows' own notification stacking near the tray.
        if (_stack.Children.Count >= MaxVisible)
            _stack.Children.RemoveAt(_growUpward ? 0 : _stack.Children.Count - 1);

        var accent = BrushFromHex(accentHex, Color.FromRgb(0x2B, 0xC0, 0xE4));

        // Avatar column + text column, Discord-style. The avatar centres against a short one-or-two
        // line card, but pins to the top of a tall bundled one (centring a 40px avatar against a
        // 190px scrolling list would float it in the middle of nowhere).
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var avatarVisual = BuildAvatar(title, accent, avatar);
        avatarVisual.VerticalAlignment = avatarAlign;
        Grid.SetColumn(avatarVisual, 0);

        var textColumn = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = avatarAlign == VerticalAlignment.Center ? VerticalAlignment.Center : VerticalAlignment.Top,
        };
        textColumn.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = new SolidColorBrush(TitleFg),
            TextWrapping = TextWrapping.Wrap,
        });
        if (body is not null)
        {
            // Auto scrollbar + a hard MaxHeight is what stops a big bundled alert (a PI refresh can
            // raise a dozen colonies at once) from running the full height of the screen. Hovering
            // pauses the dismiss timer (see below) so there's time to actually read and scroll it.
            textColumn.Children.Add(new ScrollViewer
            {
                Content = body,
                MaxHeight = BodyMaxHeightDip,
                Margin = new Thickness(0, 3, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 4, 0),
            });
        }
        Grid.SetColumn(textColumn, 1);

        grid.Children.Add(avatarVisual);
        grid.Children.Add(textColumn);

        var slide = new TranslateTransform(_toastWidthDip * 0.35, 0);
        var card = new Border
        {
            Background = new SolidColorBrush(CardBg),
            BorderBrush = new SolidColorBrush(CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, GapDip),
            Width = _toastWidthDip,
            Opacity = 0,
            RenderTransform = slide,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 3, Opacity = 0.55 },
            Child = grid,
        };

        var dismiss = new DispatcherTimer { Interval = Lifetime };
        var closed = false;
        void Close()
        {
            if (closed) return;
            closed = true;
            dismiss.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
            fadeOut.Completed += (_, _) =>
            {
                _stack.Children.Remove(card);
                UpdateBounds();
            };
            card.BeginAnimation(OpacityProperty, fadeOut);
        }

        // Hover + click affordance, Discord-style: the whole card is the hit target.
        // Hovering also PINS the card -- the dismiss timer stops while the cursor is over it and
        // restarts on leave, so a long bundled alert can actually be read and scrolled instead of
        // vanishing mid-scroll. A card already fading out is past saving; Close() is one-way.
        card.MouseEnter += (_, _) =>
        {
            card.Background = new SolidColorBrush(CardBgHover);
            if (!closed) dismiss.Stop();
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = new SolidColorBrush(CardBg);
            if (!closed) { dismiss.Stop(); dismiss.Start(); } // restart the full lifetime, not the remainder
        };
        if (onClick is not null)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) =>
            {
                onClick();
                Close();
            };
        }

        if (_growUpward) _stack.Children.Add(card); else _stack.Children.Insert(0, card);
        UpdateBounds();

        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(slide.X, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        dismiss.Tick += (_, _) => Close();
        dismiss.Start();
    }

    // Round character portrait where we have one, else a filled accent circle with the title's first
    // letter -- PI/system alerts aren't tied to a single character's face.
    private static FrameworkElement BuildAvatar(string title, SolidColorBrush accent, ImageSource? avatar)
    {
        if (avatar is not null)
        {
            return new Ellipse
            {
                Width = AvatarDip,
                Height = AvatarDip,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new ImageBrush(avatar) { Stretch = Stretch.UniformToFill },
                Stroke = accent,
                StrokeThickness = 2,
            };
        }

        var initial = string.IsNullOrWhiteSpace(title) ? "!" : title.TrimStart('"').Substring(0, 1).ToUpperInvariant();
        return new Grid
        {
            Width = AvatarDip,
            Height = AvatarDip,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Ellipse { Fill = accent },
                new TextBlock
                {
                    Text = initial,
                    FontSize = 17,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x16, 0x20)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
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
