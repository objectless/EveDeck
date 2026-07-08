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
    private readonly HashSet<int> _hiddenTiles = new();                      // positions with no live source
    private readonly int _physX, _physY, _physWidth, _physHeight;

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

        UnregisterThumbnail(position);

        if (sourceHwnd == 0)
        {
            if (_hiddenTiles.Add(position)) Invalidate(rect);
            return;
        }

        if (Win32Native.DwmRegisterThumbnail(Handle, sourceHwnd, out var id) != 0)
        {
            Log?.Invoke($"DWM thumbnail registration failed for tile {position}.");
            if (_hiddenTiles.Add(position)) Invalidate(rect);
            return;
        }

        _thumbnails[position] = id;
        var props = new Win32Native.DwmThumbnailProperties
        {
            dwFlags = Win32Native.DwmTnpRectDestination | Win32Native.DwmTnpVisible,
            rcDestination = new Win32Native.NativeRect
            {
                Left = rect.Left,
                Top = rect.Top,
                Right = rect.Right,
                Bottom = rect.Bottom
            },
            fVisible = true
        };
        if (Win32Native.DwmUpdateThumbnailProperties(id, ref props) != 0)
        {
            Log?.Invoke($"DWM thumbnail properties update failed for tile {position}.");
            UnregisterThumbnail(position);
            if (_hiddenTiles.Add(position)) Invalidate(rect);
            return;
        }

        if (_hiddenTiles.Remove(position)) Invalidate(rect);
    }

    // Focus-gated stacking: topmost while EVE / EveDeck is foreground so the previews cover other
    // apps, sunk to the bottom otherwise so they never float over the user's browser. This is the
    // ONLY z-order call in the whole overlay subsystem, and it runs on focus transitions -- not on
    // a timer -- so there is nothing left to flicker.
    public void SetZ(bool topmost)
    {
        if (!IsHandleCreated) return;
        const uint flags = Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate;
        if (topmost)
        {
            Win32Native.SetWindowPos(Handle, Win32Native.HwndTopmost, 0, 0, 0, 0, flags);
        }
        else
        {
            // HWND_BOTTOM alone won't clear the WS_EX_TOPMOST band, so drop topmost first, then sink.
            Win32Native.SetWindowPos(Handle, Win32Native.HwndNotTopmost, 0, 0, 0, 0, flags);
            Win32Native.SetWindowPos(Handle, Win32Native.HwndBottom, 0, 0, 0, 0, flags);
        }
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
            e.Graphics.FillRectangle(fill, rect);
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
