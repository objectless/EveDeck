using EveDeck.Services;
using EveDeck.Utilities;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace EveDeck.Views;

// One borderless window that hosts EVERY corner preview as a DWM thumbnail. This replaces the old
// one-WPF-window-per-tile design: with a single HWND there is no tile-vs-tile or tile-vs-label
// z-order to maintain, which is what eliminated the label/preview flicker class -- DWM composites
// the whole surface (background + all thumbnails) atomically.
//
// Technique (proven for years by EVE-O Preview): a layered window. DwmRegisterThumbnail happily
// registers many source windows into one destination window at different dest rects, and DWM
// composites them on top of whatever this window itself renders.
//
// The window's own content (the tile "backplates") is pushed via UpdateLayeredWindow with real
// per-pixel alpha, not the simpler SetLayeredWindowAttributes color-key trick. Color-keying only
// gives a binary transparent/opaque pixel -- the backplate would always be 100% opaque wherever it
// isn't the exact key color, so lowering the opacity slider only faded each DWM thumbnail while the
// backplate underneath it stayed solid, dominating the blend and masking whatever was genuinely
// behind (the desktop, or another overlapping tile like the master rect). Per-pixel alpha lets the
// backplate itself fade, so DWM's thumbnail compositing -- which blends each thumbnail against
// whatever is already beneath it, thumbnail or backplate -- can actually reveal what's underneath.
internal sealed class TileSurfaceWindow : WinForms.Form
{
    // Optional log sink set once by the view-model so overlay diagnostics surface in the Logs tab.
    public static Action<string>? Log;

    // Raised with the tile's position id on a left-click anywhere on that tile. The view-model
    // centres the tile's current occupant (a focus switch -- never input forwarded into the EVE
    // client). See COMPLIANCE.md.
    public Action<int>? TileClicked;

    private static readonly Drawing.Color TileFill = Drawing.Color.FromArgb(8, 10, 13);

    private readonly Dictionary<int, Drawing.Rectangle> _tiles = new();      // client-relative rects
    private readonly Dictionary<int, nint> _thumbnails = new();              // DWM thumbnail ids
    private readonly Dictionary<int, nint> _sources = new();                 // current source hwnd per tile
    private readonly HashSet<int> _hiddenTiles = new();                      // positions with no live source
    private readonly int _physX, _physY, _physWidth, _physHeight;

    // Real per-frame GPU capture (Windows.Graphics.Capture via Vortice Direct3D11/Direct2D1), used in
    // preference to DwmRegisterThumbnail for sharper previews with controllable resize quality --
    // DWM's own thumbnail scaling has no filter control and looks soft at small tile sizes or when
    // magnified (ZoomTile). Null when unavailable (old GPU/driver), in which case every tile silently
    // falls back to the existing DWM thumbnail path below. Captured frames are pulled by the pump
    // timer and blitted into THIS window's own composited bitmap in Redraw() -- never drawn via a
    // separate per-tile window -- so the single-surface/no-per-tick-z-order architecture that killed
    // the historical preview flicker (see project-overlay-single-surface memory) stays intact.
    private readonly WindowCaptureService? _captureService;
    private readonly Dictionary<int, CaptureSession> _captureSessions = new();
    private readonly WinForms.Timer _capturePump = new() { Interval = 66 }; // ~15 fps

    // Opacity (0-255), applied to every tile including the master/center one: as DWM_TNP_OPACITY on
    // each live thumbnail (so overlapping tiles blend against each other/the master rect, not just
    // against the desktop) AND as the backplate's own per-pixel alpha (so the backplate stops being
    // an opaque floor everything else blends against). Defaults to fully opaque; SetOpacity
    // re-applies both immediately.
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
        Bounds = new Drawing.Rectangle(physX, physY, physWidth, physHeight);
        MouseDown += OnMouseDown;

        _captureService = WindowCaptureService.TryCreate(msg => Log?.Invoke(msg));
        _capturePump.Tick += (_, _) => { if (_captureSessions.Count > 0) Redraw(); };
        _capturePump.Start();
    }

    // Never steal focus from the EVE client the user is playing.
    protected override bool ShowWithoutActivation => true;

    protected override WinForms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= unchecked((int)(Win32Native.WsExNoActivate | Win32Native.WsExToolWindow | Win32Native.WsExLayered));
            return cp;
        }
    }

    // The window's visible content is pushed entirely through UpdateLayeredWindow (see Redraw);
    // WM_PAINT/WM_ERASEBKGND never contribute pixels, so both are no-ops.
    protected override void OnPaint(WinForms.PaintEventArgs e) { }
    protected override void OnPaintBackground(WinForms.PaintEventArgs e) { }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Pin to the exact physical rect via Win32 -- WinForms bounds can be rescaled by the
        // framework's PerMonitorV2 handling; the layout math is all physical pixels.
        Win32Native.SetWindowPos(Handle, Win32Native.HwndBottom, _physX, _physY, _physWidth, _physHeight,
            Win32Native.SwpNoActivate);
        Redraw();
    }

    // Registers a tile rect (physical screen coords). Call once per position after Show().
    public void AddTile(int position, int physX, int physY, int physWidth, int physHeight)
    {
        _tiles[position] = new Drawing.Rectangle(physX - _physX, physY - _physY, physWidth, physHeight);
        _hiddenTiles.Add(position); // invisible until a source is set
        Redraw();
    }

    // Points the tile's preview at the given EVE window (0 hides the tile until a client returns).
    public void SetSource(int position, nint sourceHwnd)
    {
        if (!_tiles.TryGetValue(position, out var rect)) return;

        if (_zoomedPosition == position) ClearZoom(); // occupant changed under the cursor

        UnregisterThumbnail(position);
        StopCaptureSession(position);
        _sources[position] = sourceHwnd;

        if (sourceHwnd == 0)
        {
            _hiddenTiles.Add(position);
            Redraw();
            return;
        }

        // Prefer real per-frame WGC capture over DWM's thumbnail when available (see _captureService's
        // doc comment); only fall back to DwmRegisterThumbnail for this tile if capture-item creation
        // fails for this specific window (rare) or the service itself is unavailable.
        var session = _captureService?.CreateSession(sourceHwnd, msg => Log?.Invoke(msg));
        if (session is not null)
        {
            _captureSessions[position] = session;
            _hiddenTiles.Remove(position);
            Redraw(); // shows the fill backdrop immediately; the pump swaps in real pixels once the first frame arrives
            return;
        }

        if (!RegisterThumbnail(position, sourceHwnd, rect))
        {
            _hiddenTiles.Add(position);
            Redraw();
            return;
        }

        _hiddenTiles.Remove(position);
        Redraw();
    }

    private void StopCaptureSession(int position)
    {
        if (!_captureSessions.Remove(position, out var session)) return;
        session.Dispose();
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
        Redraw();
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

        // WGC-backed tiles need no DWM re-registration -- Redraw's capture branch just starts asking
        // the session for frames resized to _zoomedRect instead of the tile's normal rect, and the
        // manual GPU resize (see WindowCaptureService.ResizeToBitmap) is exactly what makes the
        // magnified preview sharp instead of soft.
        if (_captureSessions.ContainsKey(position))
        {
            _zoomedPosition = position;
            _zoomedRect = zoom;
            Redraw();
            return;
        }

        UnregisterThumbnail(position);
        if (!RegisterThumbnail(position, src, zoom))
        {
            // Restore the normal tile on failure rather than leaving the position blank.
            RegisterThumbnail(position, src, rect);
            return;
        }

        _zoomedPosition = position;
        _zoomedRect = zoom;
        Redraw();
    }

    public void ClearZoom()
    {
        if (_zoomedPosition < 0) return;
        var position = _zoomedPosition;
        _zoomedPosition = -1;

        if (_captureSessions.ContainsKey(position))
        {
            Redraw(); // next pump pulls frames sized to the tile's normal rect again
            return;
        }

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
        Redraw();
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

    // Pushes the backplates for every visible tile through UpdateLayeredWindow as a 32bpp ARGB
    // bitmap: transparent everywhere except the tile rects, which get TileFill at _opacity's alpha.
    // DWM thumbnails aren't part of this bitmap -- they composite on top of it every frame via their
    // own DWM_TNP_OPACITY, independent of this window's own rendering.
    private void Redraw()
    {
        if (!IsHandleCreated) return;

        var fillColor = Drawing.Color.FromArgb(_opacity, TileFill.R, TileFill.G, TileFill.B);
        using var bitmap = new Drawing.Bitmap(_physWidth, _physHeight, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(Drawing.Color.Transparent);
            using var fill = new Drawing.SolidBrush(fillColor);
            using var opacityAttrs = _opacity < 255 ? OpacityAttributes(_opacity) : null;
            foreach (var (position, rect) in _tiles)
            {
                if (_hiddenTiles.Contains(position)) continue;
                var destRect = position == _zoomedPosition ? _zoomedRect : rect;

                // WGC-backed tile: blit the latest captured-and-resized frame directly (already sized
                // to destRect by ResizeToBitmap, so this is a 1:1 draw, not another resize). DWM
                // thumbnails composite themselves on top of this window's own content independently
                // (see the class doc comment) -- for a WGC tile there's no DWM registration at all, so
                // the fill rect below is only ever a placeholder until the first frame arrives.
                if (_captureSessions.TryGetValue(position, out var session))
                {
                    using var frame = session.TryGetResizedFrame(destRect.Width, destRect.Height);
                    if (frame is not null)
                    {
                        if (opacityAttrs is not null)
                            g.DrawImage(frame, destRect, 0, 0, frame.Width, frame.Height, Drawing.GraphicsUnit.Pixel, opacityAttrs);
                        else
                            g.DrawImage(frame, destRect);
                        continue;
                    }
                }

                g.FillRectangle(fill, destRect);
            }
        }

        var screenDc = Win32Native.GetDC(0);
        var memDc = Win32Native.CreateCompatibleDC(screenDc);
        var hBitmap = nint.Zero;
        var oldBitmap = nint.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Drawing.Color.FromArgb(0));
            oldBitmap = Win32Native.SelectObject(memDc, hBitmap);

            var size = new Win32Native.SizeNative { cx = _physWidth, cy = _physHeight };
            var srcPoint = new Win32Native.NativePoint { X = 0, Y = 0 };
            var dstPoint = new Win32Native.NativePoint { X = _physX, Y = _physY };
            var blend = new Win32Native.BlendFunction
            {
                BlendOp = Win32Native.AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Win32Native.AcSrcAlpha
            };
            Win32Native.UpdateLayeredWindow(Handle, screenDc, ref dstPoint, ref size, memDc, ref srcPoint, 0, ref blend, Win32Native.UlwAlpha);
        }
        finally
        {
            Win32Native.ReleaseDC(0, screenDc);
            if (oldBitmap != nint.Zero) Win32Native.SelectObject(memDc, oldBitmap);
            if (hBitmap != nint.Zero) Win32Native.DeleteObject(hBitmap);
            Win32Native.DeleteDC(memDc);
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

    // Scales a drawn image's alpha to `opacity` (0-255) via a color matrix -- the manual equivalent of
    // DWM_TNP_OPACITY for WGC-backed tiles, which have no DWM thumbnail registration to apply that to.
    private static DrawingImaging.ImageAttributes OpacityAttributes(byte opacity)
    {
        var matrix = new DrawingImaging.ColorMatrix { Matrix33 = opacity / 255f };
        var attrs = new DrawingImaging.ImageAttributes();
        attrs.SetColorMatrix(matrix, DrawingImaging.ColorMatrixFlag.Default, DrawingImaging.ColorAdjustType.Bitmap);
        return attrs;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        foreach (var id in _thumbnails.Values) Win32Native.DwmUnregisterThumbnail(id);
        _thumbnails.Clear();

        _capturePump.Stop();
        _capturePump.Dispose();
        foreach (var session in _captureSessions.Values) session.Dispose();
        _captureSessions.Clear();
        _captureService?.Dispose();

        base.OnHandleDestroyed(e);
    }
}
