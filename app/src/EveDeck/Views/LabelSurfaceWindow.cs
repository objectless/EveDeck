using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using EveDeck.Models;
using EveDeck.Utilities;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace EveDeck.Views;

// One transparent, click-through window that draws EVERY character-name label ("pill") for the
// corner layout. Replaces the old one-PillOverlay-window-per-label design.
//
// The labels cannot live inside TileSurfaceWindow because DWM thumbnails always composite ABOVE
// the destination window's own content. Instead this window is made an OWNED window of the tile
// surface (GWLP_HWNDPARENT): the window manager itself then guarantees it stays above its owner in
// the z-order, permanently, with zero per-tick maintenance -- labels can never sink behind the
// tiles, and both surfaces move between the topmost band and the bottom together (SetZ).
//
// Label appearance is chosen by AppSettings.CornerOverlayLabelStyle:
//   "Pill"     -- rounded name chip.
//   "IconText" -- character portrait + plain name with a soft shadow, no chip.
internal sealed class LabelSurfaceWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Dictionary<int, PillElement> _pills = new();
    private readonly Dictionary<int, AlertGlowElement> _glows = new();
    private readonly int _physX, _physY, _physWidth, _physHeight;
    private readonly double _dpiScale;
    private readonly bool _iconStyle;
    private readonly int _baseHeight;
    private nint _ownerHwnd;

    public LabelSurfaceWindow(int physX, int physY, int physWidth, int physHeight,
                              double dpiScale, AppSettings settings)
    {
        _physX = physX;
        _physY = physY;
        _physWidth = physWidth;
        _physHeight = physHeight;
        _dpiScale = dpiScale;
        _iconStyle = string.Equals(settings.CornerOverlayLabelStyle, "IconText", StringComparison.OrdinalIgnoreCase);
        _baseHeight = settings.CornerOverlayLabelHeight;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        Left = physX / dpiScale;
        Top = physY / dpiScale;
        Width = physWidth / dpiScale;
        Height = physHeight / dpiScale;
        Content = _canvas;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // Input-transparent + never activate, so it can't intercept clicks meant for the tiles or
        // the EVE clients underneath.
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow | Win32Native.WsExTransparent));

        if (_ownerHwnd != 0)
            Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlpHwndParent, _ownerHwnd);

        // Pin to the exact physical rect. WPF's DIP Left/Top place a top-level window through the
        // PRIMARY monitor's scale, which misplaces the surface on a monitor with a different
        // per-monitor DPI; positioning in physical pixels keeps every label aligned to its tile.
        Win32Native.SetWindowPos(hwnd, 0, _physX, _physY, _physWidth, _physHeight,
            Win32Native.SwpNoActivate | Win32Native.SwpNoZOrder);
    }

    // Owner (the tile surface). Must be set before Show(); the window manager then keeps this
    // window above the owner in the z-order automatically.
    public void SetOwner(nint ownerHwnd) => _ownerHwnd = ownerHwnd;

    public nint Handle => new WindowInteropHelper(this).Handle;

    // Mirrors TileSurfaceWindow.SetZ -- always topmost, over every app, not just while EVE/EveDeck
    // has focus. Raised AFTER the tile surface; ownership keeps this one above it in the z-order.
    public void SetZ()
    {
        var hwnd = Handle;
        if (hwnd == 0) return;
        const uint flags = Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate;
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, 0, 0, 0, 0, flags);
    }

    // Creates (or re-places) the label for a tile / master rect. Rect is physical screen pixels;
    // atTop/centered pick the strip placement exactly as the old PillOverlay did.
    public void SetPill(int key, WindowRect physRect, bool atTop, bool centered,
                        string fontFamily, double fontSize, string colorHex,
                        bool bold, bool italic, bool dropShadow, bool outline, int opacity = 100)
    {
        if (!_pills.TryGetValue(key, out var pill))
        {
            pill = new PillElement(_iconStyle, _baseHeight, _dpiScale, atTop, centered);
            _pills[key] = pill;
            _canvas.Children.Add(pill.Container);
        }
        pill.ApplyAppearance(fontFamily, fontSize, colorHex, bold, italic, dropShadow, outline, opacity);
        pill.Place(physRect.X - _physX, physRect.Y - _physY, physRect.Width, physRect.Height);
    }

    // Reposition-only, no appearance/content change -- cheap enough to call on every mouse-move
    // while a tile is being live-dragged/resized on the overlay (see TileSurfaceWindow.TileDragging).
    public void MovePill(int key, WindowRect physRect)
    {
        if (_pills.TryGetValue(key, out var pill))
            pill.Place(physRect.X - _physX, physRect.Y - _physY, physRect.Width, physRect.Height);
    }

    public void SetPillContent(int key, string text, CharacterPortrait? portrait,
                               string? systemName = null, string? systemColorHex = null)
    {
        if (_pills.TryGetValue(key, out var pill)) pill.SetContent(text, portrait, systemName, systemColorHex);
    }

    // Each PillElement subscribes to its shared, permanently-cached CharacterPortrait's
    // PropertyChanged so a background download or refresh updates the pill later. Those portrait
    // objects outlive this window (StartCornerOverlays recreates it on nearly every settings tweak),
    // so without detaching here every rebuild leaks the previous batch of pills.
    protected override void OnClosed(EventArgs e)
    {
        foreach (var pill in _pills.Values) pill.Detach();
        base.OnClosed(e);
    }

    public void SetPillAppearance(int key, string fontFamily, double fontSize, string colorHex,
                                  bool bold, bool italic, bool dropShadow, bool outline, int opacity = 100)
    {
        if (_pills.TryGetValue(key, out var pill))
        {
            pill.ApplyAppearance(fontFamily, fontSize, colorHex, bold, italic, dropShadow, outline, opacity);
            pill.RePlace();
        }
    }

    // Pulses a colored border around a tile/master rect for "something is happening to this
    // character right now" alerts (combat) -- separate from pills, keyed the same way (position id,
    // same as _cornerRects/OccupantAtPosition), drawn on this same always-above-tiles surface so it
    // is guaranteed to render over the preview thumbnail underneath.
    public void SetAlertGlow(int key, WindowRect physRect, string colorHex, bool steady)
    {
        if (!_glows.TryGetValue(key, out var glow))
        {
            glow = new AlertGlowElement();
            _glows[key] = glow;
            _canvas.Children.Add(glow.Container);
        }
        glow.Place(physRect.X - _physX, physRect.Y - _physY, physRect.Width, physRect.Height, _dpiScale);
        glow.Pulse(colorHex, steady);
    }

    public void ClearAlertGlow(int key)
    {
        if (!_glows.TryGetValue(key, out var glow)) return;
        glow.Stop();
        _canvas.Children.Remove(glow.Container);
        _glows.Remove(key);
    }

    // -- One label: a tile-width strip with a centred chip (or portrait + name) inside -------------

    private sealed class PillElement
    {
        public readonly Grid Container = new();
        private readonly Border _pill = new();
        private readonly Ellipse _portraitDot;
        private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid _textLayer = new();
        private readonly TextBlock[] _outlineCopies = new TextBlock[8];

        // Unit offsets for the 8 outline copies drawn behind the main text (a "poor man's" stroke --
        // cheap, works with any font/size, no Geometry/FormattedText needed). Scaled by font size in
        // ApplyAppearance.
        private static readonly (double dx, double dy)[] OutlineDirections =
        {
            (-1, -1), (0, -1), (1, -1),
            (-1,  0),          (1,  0),
            (-1,  1), (0,  1), (1,  1),
        };

        private readonly bool _iconStyle;
        private readonly int _baseHeight;
        private readonly double _dpiScale;
        private readonly bool _atTop;
        private readonly bool _centered;
        private double _pillHeightDip;
        private double _portraitDip;
        private CharacterPortrait? _portrait;
        private double _tileXDip, _tileYDip, _tileWDip, _tileHDip;

        public PillElement(bool iconStyle, int baseHeight, double dpiScale, bool atTop, bool centered)
        {
            _iconStyle = iconStyle;
            _baseHeight = baseHeight;
            _dpiScale = dpiScale;
            _atTop = atTop;
            _centered = centered;

            _portraitDot = new Ellipse
            {
                Width = 0,
                Height = 0,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            RenderOptions.SetBitmapScalingMode(_portraitDot, BitmapScalingMode.HighQuality);

            // Outline copies sit behind the real text in the same Grid cell, each nudged out along
            // one of the 8 compass directions by ApplyAppearance; collapsed (and free) when unused.
            foreach (var _ in OutlineDirections)
            {
                var copy = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.Black,
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false,
                };
                _textLayer.Children.Add(copy);
            }
            for (var i = 0; i < OutlineDirections.Length; i++) _outlineCopies[i] = (TextBlock)_textLayer.Children[i];
            _textLayer.Children.Add(_text); // real text last = drawn on top of the outline copies

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(_portraitDot);
            stack.Children.Add(_textLayer);
            _pill.Child = stack;
            _pill.HorizontalAlignment = HorizontalAlignment.Center;
            _pill.VerticalAlignment = VerticalAlignment.Center;

            if (_iconStyle)
            {
                _pill.Background = Brushes.Transparent;
                _pill.CornerRadius = new CornerRadius(0);
                _pill.Padding = new Thickness(6, 2, 6, 2);
            }
            else
            {
                _pill.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0D, 0x11, 0x17));
                _pill.Padding = new Thickness(12, 4, 12, 4);
                // Round the edge that faces into the tile; a centred chip is rounded all round.
                _pill.CornerRadius = centered
                    ? new CornerRadius(6)
                    : atTop ? new CornerRadius(0, 0, 6, 6) : new CornerRadius(6, 6, 0, 0);
            }

            Container.Children.Add(_pill);
        }

        // Font family, size, colour and style (bold/italic/drop shadow/outline) for this label;
        // height/portrait scale with the font so large sizes are never clipped.
        public void ApplyAppearance(string family, double fontSize, string colorHex,
                                    bool bold, bool italic, bool dropShadow, bool outline, int opacity = 100)
        {
            var fontFamily = ResolveFontFamily(family);
            var weight = bold ? FontWeights.Bold : FontWeights.SemiBold;
            var style = italic ? FontStyles.Italic : FontStyles.Normal;

            _text.FontSize = fontSize;
            _text.FontFamily = fontFamily;
            _text.FontWeight = weight;
            _text.FontStyle = style;
            _text.Foreground = BrushFromHex(colorHex, _iconStyle
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromRgb(0xE5, 0xE7, 0xEB));
            // Soft shadow keeps text legible over bright video; user-toggleable per style/seat.
            _text.Effect = dropShadow
                ? new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.9 }
                : null;

            // "Poor man's" outline: 8 black copies of the same text, nudged out by a font-scaled
            // offset in every compass direction, sitting behind the real text.
            var offset = Math.Max(1.0, fontSize * 0.06);
            for (var i = 0; i < _outlineCopies.Length; i++)
            {
                var copy = _outlineCopies[i];
                copy.FontSize = fontSize;
                copy.FontFamily = fontFamily;
                copy.FontWeight = weight;
                copy.FontStyle = style;
                copy.Text = _text.Text;
                copy.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
                var (dx, dy) = OutlineDirections[i];
                copy.RenderTransform = new TranslateTransform(dx * offset, dy * offset);
            }

            _portraitDip = _iconStyle ? fontSize + 8 : 0;
            var content = _iconStyle ? Math.Max(fontSize * 1.7, _portraitDip) : fontSize * 1.7;
            _pillHeightDip = Math.Max(_baseHeight, content) + 8;
            if (_iconStyle) ApplyPortrait();

            // Whole-label fade: multiplies uniformly across background chip, text, outline copies and
            // drop shadow since they all live under this one Container.
            Container.Opacity = Math.Clamp(opacity, 0, 100) / 100.0;
        }

        // Positions the strip within the surface canvas (tile rect given relative to the surface
        // origin, physical pixels).
        public void Place(int relPhysX, int relPhysY, int physWidth, int physHeight)
        {
            _tileXDip = relPhysX / _dpiScale;
            _tileYDip = relPhysY / _dpiScale;
            _tileWDip = physWidth / _dpiScale;
            _tileHDip = physHeight / _dpiScale;
            RePlace();
        }

        public void RePlace()
        {
            Container.Width = _tileWDip;
            Container.Height = _pillHeightDip;
            Canvas.SetLeft(Container, _tileXDip);
            Canvas.SetTop(Container, _centered
                ? _tileYDip + _tileHDip / 2.0 - _pillHeightDip / 2.0
                : _atTop ? _tileYDip : _tileYDip + _tileHDip - _pillHeightDip);
        }

        // Label plus an optional rounded character portrait (empty text hides the whole label). The
        // portrait is the seat's shared, cache-backed CharacterPortrait (see SlotAssignment.RunningPortrait) --
        // whoever is actually logged into the seat right now, not necessarily its configured main.
        public void SetContent(string label, CharacterPortrait? portrait,
                               string? systemName = null, string? systemColorHex = null)
        {
            // Build the main text as inlines so the trailing " . <system>" segment can carry its own
            // colour (deterministic per system) while the name keeps the pill's normal foreground.
            _text.Inlines.Clear();
            if (!string.IsNullOrEmpty(label))
            {
                var suffix = !string.IsNullOrEmpty(systemName) && !string.IsNullOrEmpty(systemColorHex)
                    ? " · " + systemName
                    : null;
                if (suffix is not null && label.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var main = label[..^suffix.Length];
                    if (main.Length > 0) _text.Inlines.Add(new System.Windows.Documents.Run(main));
                    _text.Inlines.Add(new System.Windows.Documents.Run(suffix)
                    {
                        Foreground = BrushFromHex(systemColorHex!, Color.FromRgb(0x9C, 0xA3, 0xAF))
                    });
                }
                else
                {
                    _text.Inlines.Add(new System.Windows.Documents.Run(label));
                }
            }
            foreach (var copy in _outlineCopies) copy.Text = label;
            Container.Visibility = string.IsNullOrWhiteSpace(label) ? Visibility.Collapsed : Visibility.Visible;

            if (!ReferenceEquals(portrait, _portrait))
            {
                if (_portrait is not null) _portrait.PropertyChanged -= OnPortraitChanged;
                _portrait = portrait;
                if (_portrait is not null) _portrait.PropertyChanged += OnPortraitChanged;
                ApplyPortrait();
            }
        }

        private void OnPortraitChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(CharacterPortrait.Image) or null) ApplyPortrait();
        }

        // Detach from the shared portrait's event so this (otherwise-dead) pill isn't kept alive by
        // it. Called when the owning LabelSurfaceWindow closes.
        public void Detach()
        {
            if (_portrait is not null) _portrait.PropertyChanged -= OnPortraitChanged;
            _portrait = null;
        }

        private void ApplyPortrait()
        {
            if (!_iconStyle || _portrait is null)
            {
                _portraitDot.Visibility = Visibility.Collapsed;
                _portraitDot.Width = _portraitDot.Height = 0;
                _portraitDot.Fill = null;
                return;
            }

            _portraitDot.Width = _portraitDot.Height = _portraitDip;
            _portraitDot.Visibility = Visibility.Visible;

            // PortraitCacheService owns downloading/decoding/freshness; the pill just reflects the
            // shared portrait's current image (null while it's still loading -- keeps showing blank
            // rather than flashing a placeholder, then fills in once ready).
            _portraitDot.Fill = _portrait.Image is { } image
                ? new ImageBrush(image) { Stretch = Stretch.UniformToFill }
                : null;
        }

        // "Acens" (the shipped default) is bundled as an app resource so it renders correctly even
        // when not installed system-wide; any other family name is resolved from installed fonts.
        private static readonly FontFamily BundledAcens = new(new Uri("pack://application:,,,/Assets/Fonts/"), "./#Acens");

        private static FontFamily ResolveFontFamily(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return new FontFamily("Segoe UI");
            return family.Equals("Acens", StringComparison.OrdinalIgnoreCase) ? BundledAcens : new FontFamily(family);
        }

        private static SolidColorBrush BrushFromHex(string hex, Color fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch { /* fall through to the style default */ }
            return new SolidColorBrush(fallback);
        }
    }

    // -- One alert glow: a pulsing colored outline covering an entire tile/master rect ------------

    private sealed class AlertGlowElement
    {
        private readonly System.Windows.Media.Effects.DropShadowEffect _bloom = new()
        {
            ShadowDepth = 0,
            BlurRadius = 18,
            Opacity = 0.9,
        };

        private readonly System.Windows.Shapes.Path _brackets = new()
        {
            Fill = null,
            StrokeThickness = 5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };

        public readonly Canvas Container = new() { IsHitTestVisible = false };

        public AlertGlowElement()
        {
            _brackets.Effect = _bloom;
            Container.Children.Add(_brackets);
        }

        public void Place(double relPhysX, double relPhysY, double physWidth, double physHeight, double dpiScale)
        {
            var w = physWidth / dpiScale;
            var h = physHeight / dpiScale;
            Container.Width = w;
            Container.Height = h;
            Canvas.SetLeft(Container, relPhysX / dpiScale);
            Canvas.SetTop(Container, relPhysY / dpiScale);
            var arm = Math.Clamp(Math.Min(w, h) * 0.18, 12.0, 48.0);
            _brackets.Data = EveDeck.Utilities.OverlayGeometry.CornerBrackets(0, 0, w, h, arm);
        }

        // steady = a calm constant highlight instead of a blink. Used in Abyss Mode, where the master
        // and up to two more characters can be under continuous, expected fire at once -- strobing all
        // of them would be obnoxious, so there the brackets just hold lit.
        public void Pulse(string colorHex, bool steady)
        {
            var color = BrushColor(colorHex);
            _brackets.Stroke = new SolidColorBrush(color);
            _bloom.Color = color;

            if (steady)
            {
                _brackets.BeginAnimation(UIElement.OpacityProperty, null);
                _bloom.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
                _brackets.Opacity = 0.85;
                _bloom.BlurRadius = 16;
                return;
            }

            // Gentle blink: a soft opacity breathe (never fully off) plus a small blur pulse, slow
            // enough to read as "under fire" without strobing.
            _brackets.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.55, 1.0, TimeSpan.FromSeconds(0.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            _bloom.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
                new DoubleAnimation(12, 26, TimeSpan.FromSeconds(0.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }

        public void Stop()
        {
            _brackets.BeginAnimation(UIElement.OpacityProperty, null);
            _bloom.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
        }

        private static Color BrushColor(string hex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                    return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch { /* fall through to the default */ }
            return Color.FromRgb(0xEF, 0x44, 0x44);
        }
    }
}
