using EveDeck.Utilities;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace EveDeck.Views;

// One borderless window that hosts EVERY corner preview as a DWM thumbnail. This replaces the old
// one-WPF-window-per-tile design: with a single HWND there is no tile-vs-tile or tile-vs-label
// z-order to maintain, which is what eliminated the label/preview flicker class -- DWM composites
// the whole surface (background + all thumbnails) atomically.
//
// Technique (proven for years by EVE-O Preview): a color-keyed layered WinForms window. Pixels
// painted with the transparency key are invisible AND click-through, so the window can span the
// whole layout monitor while only the tile rects are visible/interactive. DwmRegisterThumbnail
// happily registers many source windows into one destination window at different dest rects.
internal sealed class TileSurfaceWindow : WinForms.Form
{
    // Optional log sink set once by the view-model so overlay diagnostics surface in the Logs tab.
    public static Action<string>? Log;

    // Raised with the tile's position id on a left-click anywhere on that tile. The view-model
    // centres the tile's current occupant (a focus switch -- never input forwarded into the EVE
    // client). See COMPLIANCE.md.
    public Action<int>? TileClicked;

    // Fuchsia is the classic transparency key; it never collides with the near-black tile fill.
    private static readonly Drawing.Color KeyColor = Drawing.Color.Fuchsia;
    private static readonly Drawing.Color TileFill = Drawing.Color.FromArgb(8, 10, 13);

    private readonly Dictionary<int, Drawing.Rectangle> _tiles = new();      // client-relative rects
    private readonly Dictionary<int, nint> _thumbnails = new();              // DWM thumbnail ids
    private readonly Dictionary<int, nint> _sources = new();                 // current source hwnd per tile
    private readonly HashSet<int> _hiddenTiles = new();                      // positions with no live source
    private readonly int _physX, _physY, _physWidth, _physHeight;

    // Thumbnail opacity (0-255), applied to every tile including the master/center one. Defaults to
    // fully opaque; SetOpacity re-applies it to already-registered thumbnails immediately.
    private byte _opacity = 255;

    // Zoom-on-hover state: at most one tile is magnified at a time; its thumbnail dest rect is
    // scaled around the tile centre (clamped to the surface) and the thumbnail is re-registered so
    // it composites ABOVE its neighbours (DWM stacks thumbnails in registration order).
    private int _zoomedPosition = -1;
    private Drawing.Rectangle _zoomedRect;

    public TileSurfaceWindow(int physX, int physY, int physWidth, int physHeight)
    {
        _physX = physX;
        _physY = physY;
        _physWidth = physWidth;
        _physHeight = physHeight;

        FormBorderStyle = WinForms.FormBorderStyle.None;
        StartPosition = WinForms.FormStartPosition.Manual;
        ShowInTaskbar = false;
        AutoScaleMode = WinForms.AutoScaleMode.None;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        Bounds = new Drawing.Rectangle(physX, physY, physWidth, physHeight);
        MouseDown += OnMouseDown;
    }

    // Never steal focus from the EVE client the user is playing.
    protected override bool ShowWithoutActivation => true;

    protected override WinForms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= unchecked((int)(Win32Native.WsExNoActivate | Win32Native.WsExToolWindow));
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Pin to the exact physical rect via Win32 -- WinForms bounds can be rescaled by the
        // framework's PerMonitorV2 handling; the layout math is all physical pixels.
        Win32Native.SetWindowPos(Handle, Win32Native.HwndBottom, _physX, _physY, _physWidth, _physHeight,
            Win32Native.SwpNoActivate);
    }

    // Registers a tile rect (physical screen coords). Call once per position after Show().
    public void AddTile(int position, int physX, int physY, int physWidth, int physHeight)
    {
        _tiles[position] = new Drawing.Rectangle(physX - _physX, physY - _physY, physWidth, physHeight);
        _hiddenTiles.Add(position); // invisible until a source is set
        Invalidate(_tiles[position]);
    }

    // Points the tile's preview at the given EVE window (0 hides the tile until a client returns).
    public void SetSource(int position, nint sourceHwnd)
    {
        if (!_tiles.TryGetValue(position, out var rect)) return;

        if (_zoomedPosition == position) ClearZoom(); // occupant changed under the cursor

        UnregisterThumbnail(position);
        _sources[position] = sourceHwnd;

        if (sourceHwnd == 0)
        {
            if (_hiddenTiles.Add(position)) Invalidate(rect);
            return;
        }

        if (!RegisterThumbnail(position, sourceHwnd, rect))
        {
            if (_hiddenTiles.Add(position)) Invalidate(rect);
            return;
        }

        if (_hiddenTiles.Remove(position)) Invalidate(rect);
    }

    private bool RegisterThumbnail(int position, nint sourceHwnd, Drawing.Rectangle dest)
    {
        if (Win32Native.DwmRegisterThumbnail(Handle, sourceHwnd, out var id) != 0)
        {
            Log?.Invoke($"DWM thumbnail registration failed for tile {position}.");
            return false;
        }

        _thumbnails[position] = id;
        var props = new Win32Native.DwmThumbnailProperties
        {
            dwFlags = Win32Native.DwmTnpRectDestination | Win32Native.DwmTnpVisible | Win32Native.DwmTnpOpacity,
            rcDestination = new Win32Native.NativeRect
            {
                Left = dest.Left,
                Top = dest.Top,
                Right = dest.Right,
                Bottom = dest.Bottom
            },
            fVisible = true,
            opacity = _opacity
        };
        if (Win32Native.DwmUpdateThumbnailProperties(id, ref props) != 0)
        {
            Log?.Invoke($"DWM thumbnail properties update failed for tile {position}.");
            UnregisterThumbnail(position);
            return false;
        }
        return true;
    }

    // Preview transparency (0-100%, same convention as the label opacity slider) for every tile,
    // including the master/center one. Re-applies immediately to whatever is already registered.
    public void SetOpacity(int percent)
    {
        _opacity = (byte)(Math.Clamp(percent, 0, 100) * 255 / 100);
        foreach (var id in _thumbnails.Values)
        {
            var props = new Win32Native.DwmThumbnailProperties { dwFlags = Win32Native.DwmTnpOpacity, opacity = _opacity };
            Win32Native.DwmUpdateThumbnailProperties(id, ref props);
        }
    }

    // Magnify one tile's PREVIEW around its centre (clamped to the surface). Purely a DWM dest-rect
    // change — the real EVE window is never touched, unlike hover-peek. Re-registers the thumbnail
    // so the enlarged preview draws above its neighbours.
    public void ZoomTile(int position, double factor)
    {
        if (_zoomedPosition == position) return;
        ClearZoom();

        if (!_tiles.TryGetValue(position, out var rect) || _hiddenTiles.Contains(position)) return;
        if (!_sources.TryGetValue(position, out var src) || src == 0) return;

        var w = (int)Math.Round(rect.Width * factor);
        var h = (int)Math.Round(rect.Height * factor);
        var zoom = new Drawing.Rectangle(
            rect.Left + rect.Width / 2 - w / 2,
            rect.Top + rect.Height / 2 - h / 2, w, h);
        zoom.X = Math.Clamp(zoom.X, 0, Math.Max(0, ClientSize.Width - zoom.Width));
        zoom.Y = Math.Clamp(zoom.Y, 0, Math.Max(0, ClientSize.Height - zoom.Height));

        UnregisterThumbnail(position);
        if (!RegisterThumbnail(position, src, zoom))
        {
            // Restore the normal tile on failure rather than leaving the position blank.
            RegisterThumbnail(position, src, rect);
            return;
        }

        _zoomedPosition = position;
        _zoomedRect = zoom;
        Invalidate();
    }

    public void ClearZoom()
    {
        if (_zoomedPosition < 0) return;
        var position = _zoomedPosition;
        _zoomedPosition = -1;

        if (_tiles.TryGetValue(position, out var rect) && _thumbnails.TryGetValue(position, out var id))
        {
            var props = new Win32Native.DwmThumbnailProperties
            {
                dwFlags = Win32Native.DwmTnpRectDestination,
                rcDestination = new Win32Native.NativeRect
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Right = rect.Right,
                    Bottom = rect.Bottom
                }
            };
            Win32Native.DwmUpdateThumbnailProperties(id, ref props);
        }
        Invalidate();
    }

    // Always topmost, over EVE, EveDeck, and every other app (browser, Discord, etc.) alike -- the
    // overlay is meant to stay visible no matter what has focus. Re-asserting HWND_TOPMOST is only
    // ever called from event-driven triggers (surface creation, layout/swap changes, the foreground
    // WinEvent hook), never on an unconditional per-tick timer, which is what caused the historical
    // self-inflicted raise/bury/raise flicker (see project-seat-order-and-frame-flicker memory).
    public void SetZ()
    {
        if (!IsHandleCreated) return;
        const uint flags = Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate;
        Win32Native.SetWindowPos(Handle, Win32Native.HwndTopmost, 0, 0, 0, 0, flags);
    }

    protected override void OnPaint(WinForms.PaintEventArgs e)
    {
        base.OnPaint(e);
        // Tile backplates: the near-black fill shows through letterboxing and -- critically -- gives
        // the tile area non-keyed pixels, so it hit-tests as solid (clicks land here) while the rest
        // of the surface stays click-through. The DWM thumbnails composite on top of this fill.
        using var fill = new Drawing.SolidBrush(TileFill);
        foreach (var (position, rect) in _tiles)
        {
            if (_hiddenTiles.Contains(position)) continue;
            e.Graphics.FillRectangle(fill, position == _zoomedPosition ? _zoomedRect : rect);
        }
    }

    private void OnMouseDown(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button != WinForms.MouseButtons.Left) return;
        foreach (var (position, rect) in _tiles)
        {
            if (_hiddenTiles.Contains(position) || !rect.Contains(e.Location)) continue;
            try { TileClicked?.Invoke(position); } catch { } // subscriber exceptions must not kill the input loop
            return;
        }
    }

    private void UnregisterThumbnail(int position)
    {
        if (!_thumbnails.TryGetValue(position, out var id)) return;
        Win32Native.DwmUnregisterThumbnail(id);
        _thumbnails.Remove(position);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        foreach (var id in _thumbnails.Values) Win32Native.DwmUnregisterThumbnail(id);
        _thumbnails.Clear();
        base.OnHandleDestroyed(e);
    }
}
