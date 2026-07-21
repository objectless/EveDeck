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

    // Raised on Shift+left-click instead of TileClicked -- the view-model toggles the tile's
    // occupant out of cycling (Cycle/CycleGroup hotkeys) without unassigning it, mirroring EVE-O
    // Preview's shift+click cycle-group toggle. Still a local UI gesture, never input forwarded
    // into the EVE client. See COMPLIANCE.md.
    public Action<int>? TileShiftClicked;

    private static readonly Drawing.Color TileFill = Drawing.Color.FromArgb(8, 10, 13);

    private readonly Dictionary<int, Drawing.Rectangle> _tiles = new();      // client-relative rects
    private readonly Dictionary<int, nint> _thumbnails = new();              // DWM thumbnail ids
    private readonly Dictionary<int, nint> _sources = new();                 // current source hwnd per tile
    private readonly HashSet<int> _hiddenTiles = new();                      // positions with no live source
    private readonly HashSet<int> _preventedPositions = new();               // positions showing a plain placeholder, no live capture
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
    private readonly Dictionary<int, ITileCaptureSession> _captureSessions = new();
    private readonly WinForms.Timer _capturePump = new() { Interval = 66 }; // ~15 fps

    // Reused across Redraws instead of allocating a full-surface (~14MB at 2560x1440) bitmap every
    // frame -- at the pump's ~15fps that was ~220MB/s of large-object-heap churn feeding the GPU/
    // memory pressure behind the GetHbitmap "lack of memory" failures (see the pump comment above).
    // Fixed size (the surface never resizes), so allocated once on first use.
    private Drawing.Bitmap? _backBuffer;

    // True when at least one capture session has a frame the compositor hasn't drawn yet -- drives
    // the pump's redraw gating so we don't recomposite when nothing changed.
    private bool ConsumeAnyFrameDirty()
    {
        var any = false;
        foreach (var session in _captureSessions.Values)
            if (session.ConsumeFrameDirty()) any = true; // consume ALL, don't short-circuit
        return any;
    }

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

    // Ad-hoc live drag/resize directly on the overlay (right-drag = move, both buttons + drag =
    // resize), mirroring EVE-O Preview/EVE-APM Preview's tile manipulation -- deliberately NOT
    // left-click, which stays click-to-focus, or shift+left-click, which stays cycle-exclude toggle.
    private enum TileDragMode { None, Reposition, Resize }
    private const int MinTileSize = 40;
    private TileDragMode _dragMode = TileDragMode.None;
    private int _dragPosition = -1;
    private Drawing.Point _dragStartMouse;
    private Drawing.Rectangle _dragStartRect;

    // Raised once, on mouse-up, with the FINAL physical-screen rect after a drag/resize completes.
    // Never raised while dragging -- the view-model persists this into the active layout profile
    // (cloning a built-in first), which is comparatively expensive and only belongs at the end of a
    // gesture.
    public Action<int, int, int, int, int>? TileRectChanged;

    // Raised once when a drag/resize begins -- the view-model reverts any active hover-peek/zoom
    // through the same path it already uses when the cursor leaves a tile, since the hit-test loop
    // that would normally notice "cursor left the peeked tile" gets suppressed for the whole drag
    // (see IsDragging below) and would otherwise leave a real-window peek-swap stuck mid-flight.
    public Action<int>? TileDragStarted;

    // Raised on every mouse-move while dragging, with the CURRENT (not yet final) physical rect --
    // cheap, in-memory-only feedback (no profile persistence) so the pill label visually follows the
    // tile instead of staying behind at its pre-drag position.
    public Action<int, int, int, int, int>? TileDragging;

    // Lets the view-model suppress hover-driven peek/zoom for the whole overlay while a drag/resize
    // is in progress -- moving the mouse across other tiles mid-drag shouldn't also trigger them.
    public bool IsDragging => _dragMode != TileDragMode.None;

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
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;

        _captureService = WindowCaptureService.TryCreate(msg => Log?.Invoke(msg));
        _capturePump.Tick += (_, _) =>
        {
            // A zoomed tile can now grow over master's screen area, so a topmost-pinned master (or
            // anything else re-asserting its own topmost state, e.g. EVE's own window management)
            // can win the z-order race against a single one-shot reassertion made when the zoom
            // started -- found live 2026-07-19, needed manually unpinning master to "fix" it. Keep
            // re-asserting at the pump's own cadence (~66ms) for as long as the zoom is active,
            // rather than trying to guarantee we always go last relative to whatever else is
            // repinning topmost. Gated to zoom-active only; SetZ() is a no-op cost otherwise avoided.
            if (_zoomedPosition >= 0) SetZ();
            // Only recomposite when a capture session actually produced a NEW frame this tick.
            // Previously this redrew unconditionally at ~15fps whenever any capture session existed,
            // which meant a full-surface (up to 2560x1440x4 = ~14MB) bitmap allocation + GetHbitmap
            // (a device-dependent-bitmap allocation that touches the graphics driver) every 66ms even
            // when nothing changed. On a VRAM-constrained setup (e.g. EVE in DX12 with upscaling +
            // frame generation across several clients) that steady churn was enough to make GetHbitmap
            // fail with "lack of memory" and, worse, starve the EVE clients of GPU resources -- found
            // live 2026-07-20, a client went white/needed force-closing while docking (a VRAM spike).
            // Explicit redraws (opacity, drag, zoom, source change) still call Redraw() directly.
            if (ConsumeAnyFrameDirty()) Redraw();
        };
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

    private const int WmDwmCompositionChanged = 0x031E;
    private const int WmDisplayChange = 0x007E;

    // DWM can silently drop every live DwmRegisterThumbnail/WGC registration at once -- sleep/wake,
    // a resolution or monitor-topology change, an RDP session connect/disconnect, or a GPU driver
    // reset (TDR) all leave the previously-registered sources stale, so every tile goes solid black
    // (the backplate fill) while the pill labels keep drawing fine (LabelSurfaceWindow paints its
    // own content, nothing to invalidate). Found live 2026-07-19: previews stayed blank for a full
    // minute of real play until an unrelated action (opening the tray icon's context menu) happened
    // to force DWM to recomposite the desktop and "un-stuck" it -- that's an incidental trigger, not
    // a real fix, so react to the actual OS broadcasts instead of relying on something else nudging
    // the compositor. Both messages are broadcast to every top-level window, so no extra hook target
    // is needed.
    protected override void WndProc(ref WinForms.Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmDwmCompositionChanged || m.Msg == WmDisplayChange) RefreshAllSources();
    }

    // Re-runs SetSource for every currently-assigned tile, forcing a full unregister+reregister of
    // its DWM thumbnail or WGC capture session. Deliberately only called from the WndProc hook above
    // (a genuinely rare system event), never on a timer -- an unconditional periodic re-register is
    // exactly what caused the historical "previews randomly refresh" bug (see
    // project-thumbnail-random-refresh memory): unregister+reregister is visibly a brief blink, so it
    // must stay event-driven, not polled.
    private void RefreshAllSources()
    {
        if (_sources.Count == 0) return;
        Log?.Invoke("DWM composition change detected -- re-registering all corner preview sources.");
        foreach (var (position, hwnd) in _sources.ToArray())
            SetSource(position, hwnd);
    }

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

    // Toggles whether a position shows its real live preview or just the plain fill placeholder --
    // e.g. for a cloaky alt the user doesn't want visible even as a thumbnail. Re-runs SetSource for
    // whatever's currently assigned there so the capture registration state updates immediately
    // instead of waiting for the next actual source-handle change.
    public void SetPreviewPrevented(int position, bool prevented)
    {
        if (prevented) _preventedPositions.Add(position); else _preventedPositions.Remove(position);
        if (_sources.TryGetValue(position, out var hwnd)) SetSource(position, hwnd);
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

        // Prevented positions stay "visible" (so the plain fill placeholder draws, matching the
        // fallback DrawTile already uses when there's no capture session) but never get a real
        // DWM/WGC registration -- see SetPreviewPrevented.
        if (_preventedPositions.Contains(position))
        {
            _hiddenTiles.Remove(position);
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
            // Neither WGC nor DWM could produce a live thumbnail for this window -- some RDP/remote-
            // desktop sessions and certain virtual-display setups fail both. Fall back to periodic
            // PrintWindow-based screenshot capture rather than leaving the tile blank (matches EVE-O
            // Preview's "Compatibility Mode"). Genuinely last-resort: only reached when the two
            // faster paths above have already failed for this specific window.
            _captureSessions[position] = new ScreenshotCaptureSession(sourceHwnd, msg => Log?.Invoke(msg));
            _hiddenTiles.Remove(position);
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

    // Cheap live reposition/resize during a drag -- just the dest-rect flag, not a full
    // unregister+reregister (which RegisterThumbnail does, but that's overkill on every mouse-move).
    private void UpdateThumbnailRect(nint thumbnailId, Drawing.Rectangle dest)
    {
        var props = new Win32Native.DwmThumbnailProperties
        {
            dwFlags = Win32Native.DwmTnpRectDestination,
            rcDestination = new Win32Native.NativeRect { Left = dest.Left, Top = dest.Top, Right = dest.Right, Bottom = dest.Bottom },
        };
        Win32Native.DwmUpdateThumbnailProperties(thumbnailId, ref props);
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
        try
        {
            RedrawCore();
        }
        catch (Exception ex)
        {
            // Final safety net around the whole redraw pass -- DrawTile's own try/catch (below)
            // handles the specific crash found live (an unhandled GDI+ ArgumentException that took
            // down the whole app via the native JIT-debug dialog, since WPF's
            // DispatcherUnhandledException doesn't reach a WinForms NativeWindow.Callback), but any
            // OTHER unexpected failure in bitmap allocation, GetHbitmap, or the UpdateLayeredWindow
            // push must never crash the app either. Skip this frame; the next pump tick retries.
            Log?.Invoke($"Redraw failed: {ex}");
        }
    }

    private void RedrawCore()
    {
        if (!IsHandleCreated) return;

        var fillColor = Drawing.Color.FromArgb(_opacity, TileFill.R, TileFill.G, TileFill.B);
        // Reuse the back-buffer across frames instead of allocating a fresh ~14MB bitmap each Redraw
        // (see field comment) -- cleared below so each frame starts fully transparent as before.
        var bitmap = _backBuffer ??= new Drawing.Bitmap(_physWidth, _physHeight, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(Drawing.Color.Transparent);
            using var fill = new Drawing.SolidBrush(fillColor);
            using var opacityAttrs = _opacity < 255 ? OpacityAttributes(_opacity) : null;

            // WGC-backed tiles are blitted straight into this shared bitmap (unlike DWM thumbnails,
            // which composite themselves via their own registration order independently of this draw
            // loop -- see the class doc comment), so THIS loop's order is what determines which tile
            // wins any overlap. The zoomed tile can now grow over its neighbours (master-avoidance
            // clamping was removed, see project-overlay-wgc-thumbnails memory), so it must always be
            // drawn last regardless of dictionary iteration order, or a sibling tile drawn afterward
            // paints over the part of it that overlaps -- found live 2026-07-19.
            void DrawTile(int position, Drawing.Rectangle rect)
            {
                if (_hiddenTiles.Contains(position)) return;
                var destRect = position == _zoomedPosition ? _zoomedRect : rect;

                try
                {
                    if (_captureSessions.TryGetValue(position, out var session))
                    {
                        using var frame = session.TryGetResizedFrame(destRect.Width, destRect.Height);
                        if (frame is not null)
                        {
                            if (opacityAttrs is not null)
                                g.DrawImage(frame, destRect, 0, 0, frame.Width, frame.Height, Drawing.GraphicsUnit.Pixel, opacityAttrs);
                            else
                                g.DrawImage(frame, destRect);
                            return;
                        }
                    }

                    g.FillRectangle(fill, destRect);
                }
                catch (Exception ex)
                {
                    // Found live (2026-07-19): an unhandled ArgumentException here ("Parameter is not
                    // valid" -- GDI+'s signature message for touching an already-disposed Bitmap/Image)
                    // crashed the whole app via the native "unhandled exception" JIT-debug dialog --
                    // WPF's DispatcherUnhandledException (App.xaml.cs) does not catch exceptions
                    // escaping a WinForms NativeWindow.Callback, which is what a message-pump-triggered
                    // Redraw runs under. One bad frame for one tile must never take down the whole
                    // process; skip this tile for this pass (next pump tick tries again) and log full
                    // detail (destRect + whichever path was taken) so a recurrence is diagnosable
                    // instead of just another crash dialog.
                    Log?.Invoke($"Redraw failed for tile {position} (destRect={destRect}): {ex}");
                }
            }

            foreach (var (position, rect) in _tiles)
            {
                if (position != _zoomedPosition) DrawTile(position, rect);
            }
            if (_zoomedPosition >= 0 && _tiles.TryGetValue(_zoomedPosition, out var zoomedRect))
                DrawTile(_zoomedPosition, zoomedRect);
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
        var buttons = WinForms.Control.MouseButtons;
        if (buttons.HasFlag(WinForms.MouseButtons.Left) && buttons.HasFlag(WinForms.MouseButtons.Right))
        {
            // Both buttons down (regardless of which was pressed first) -- upgrade an in-progress
            // reposition drag to resize in place (no re-hit-test: the mouse may already have moved
            // off the tile's original rect since the first button went down), or start a fresh resize
            // if neither button's own mouse-down already claimed one.
            if (_dragPosition >= 0) BeginDrag(TileDragMode.Resize, _dragPosition, _tiles[_dragPosition], e.Location);
            else if (TryFindTileAt(e.Location, out var position, out var rect)) BeginDrag(TileDragMode.Resize, position, rect, e.Location);
            return;
        }

        if (e.Button == WinForms.MouseButtons.Right)
        {
            if (TryFindTileAt(e.Location, out var position, out var rect)) BeginDrag(TileDragMode.Reposition, position, rect, e.Location);
            return;
        }

        if (e.Button != WinForms.MouseButtons.Left) return;
        if (!TryFindTileAt(e.Location, out var clickedPosition, out _)) return;
        var shift = WinForms.Control.ModifierKeys.HasFlag(WinForms.Keys.Shift);
        try { (shift ? TileShiftClicked : TileClicked)?.Invoke(clickedPosition); } catch { } // subscriber exceptions must not kill the input loop
    }

    private bool TryFindTileAt(Drawing.Point location, out int position, out Drawing.Rectangle rect)
    {
        foreach (var (pos, r) in _tiles)
        {
            if (_hiddenTiles.Contains(pos) || !r.Contains(location)) continue;
            position = pos;
            rect = r;
            return true;
        }
        position = -1;
        rect = default;
        return false;
    }

    private void BeginDrag(TileDragMode mode, int position, Drawing.Rectangle rect, Drawing.Point mouseLocation)
    {
        // Zoom-style peek is cleared here directly (own state); a Peek-style (real-window swap) peek
        // is the view-model's to revert, via TileDragStarted below -- it needs to go through
        // RevertPeekSwap, not something this class can do on its own.
        if (_zoomedPosition == position) ClearZoom();
        _dragMode = mode;
        _dragPosition = position;
        _dragStartRect = rect;
        _dragStartMouse = mouseLocation;
        Cursor = mode == TileDragMode.Resize ? WinForms.Cursors.SizeNWSE : WinForms.Cursors.SizeAll;
        try { TileDragStarted?.Invoke(position); } catch { } // subscriber exceptions must not kill the input loop
    }

    private void OnMouseMove(object? sender, WinForms.MouseEventArgs e)
    {
        if (_dragMode == TileDragMode.None || _dragPosition < 0) return;
        var dx = e.X - _dragStartMouse.X;
        var dy = e.Y - _dragStartMouse.Y;

        var newRect = _dragStartRect;
        if (_dragMode == TileDragMode.Reposition)
        {
            newRect.X = Math.Clamp(_dragStartRect.X + dx, 0, Math.Max(0, ClientSize.Width - newRect.Width));
            newRect.Y = Math.Clamp(_dragStartRect.Y + dy, 0, Math.Max(0, ClientSize.Height - newRect.Height));
        }
        else // Resize
        {
            newRect.Width = Math.Max(MinTileSize, Math.Min(_dragStartRect.Width + dx, ClientSize.Width - newRect.X));
            newRect.Height = Math.Max(MinTileSize, Math.Min(_dragStartRect.Height + dy, ClientSize.Height - newRect.Y));
        }

        _tiles[_dragPosition] = newRect;
        // DWM's dest rect updates live via DwmUpdateThumbnailProperties -- no need to unregister and
        // re-register the thumbnail on every mouse-move tick. WGC/screenshot-backed tiles need no
        // equivalent call at all: Redraw() already reads destRect fresh from _tiles every pass.
        if (_thumbnails.TryGetValue(_dragPosition, out var thumbId)) UpdateThumbnailRect(thumbId, newRect);
        Redraw();

        // Cheap, in-memory-only feedback -- the pill label lives on a separate window (LabelSurfaceWindow)
        // and has no idea this tile moved unless told every frame; TileRectChanged only fires once, on
        // mouse-up, since IT persists to the profile (comparatively expensive).
        try { TileDragging?.Invoke(_dragPosition, newRect.X + _physX, newRect.Y + _physY, newRect.Width, newRect.Height); } catch { }
    }

    private void OnMouseUp(object? sender, WinForms.MouseEventArgs e)
    {
        if (_dragMode == TileDragMode.None || _dragPosition < 0) return;
        var position = _dragPosition;
        var finalRect = _tiles[position];
        _dragMode = TileDragMode.None;
        _dragPosition = -1;
        Cursor = WinForms.Cursors.Default;

        // Physical-screen coords, matching AddTile's own convention -- the view-model persists this
        // into the active layout profile (cloning a built-in first if needed).
        try { TileRectChanged?.Invoke(position, finalRect.X + _physX, finalRect.Y + _physY, finalRect.Width, finalRect.Height); }
        catch { } // subscriber exceptions must not kill the input loop
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
        _backBuffer?.Dispose();
        _backBuffer = null;

        base.OnHandleDestroyed(e);
    }
}
