using System.Drawing;
using System.Drawing.Imaging;

namespace EveDeck.Services;

// Corner-tile preview capture.
//
// HISTORY -- read before reintroducing GPU capture here. Between 2026-07-18 and 2026-07-21 this file
// hosted a Windows.Graphics.Capture (WGC) + Vortice Direct3D11/Direct2D1 pipeline that pulled real
// per-frame GPU captures for sharper previews. It was removed entirely after causing escalating
// failures on a VRAM-heavy setup (EVE running DX12 with upscaling + frame generation across several
// clients): first a leaked Direct3D11CaptureFramePool on every failed session construction, then
// GetHbitmap "lack of memory" failures with an EVE client going white and needing force-close while
// docking, and finally a full machine hard-lock requiring a reboot. Standing up a second D3D11
// device and moving per-frame GPU textures alongside several DX12 clients is simply too much GPU/
// VRAM contention, and no amount of tuning the readback made that fundamental problem go away.
//
// EveDeck now does exactly what EVE-O Preview and EVE-APM Preview do: DwmRegisterThumbnail only (see
// TileSurfaceWindow). DWM is already running and already compositing every one of those windows, so
// a registered thumbnail costs essentially no extra GPU -- no second device, no per-frame texture,
// nothing that can starve the game. Those tools have done it that way for years with crisp results.
//
// What remains here is the last-resort fallback below, for windows DWM cannot thumbnail at all.

// Common surface TileSurfaceWindow's Redraw pulls frames through. Only the PrintWindow fallback
// implements it now -- DWM-backed tiles composite themselves and never go through Redraw at all.
internal interface ITileCaptureSession : IDisposable
{
    Bitmap? TryGetResizedFrame(int destWidth, int destHeight);

    // True (and resets) when this session has produced a new frame the compositor hasn't drawn yet,
    // so the pump can skip a full recomposite + GetHbitmap when nothing changed.
    bool ConsumeFrameDirty();
}

// Last-resort capture path for a window DWM cannot produce a live thumbnail for -- some RDP/remote-
// desktop sessions and certain virtual-display setups fail (matches EVE-O Preview's "Compatibility
// Mode"). Uses PrintWindow, which works over RDP because it asks the target process to paint itself
// into a provided DC rather than reading a live composited surface. Deliberately slow (recaptures at
// most once a second, like EVE-O's own 1fps compatibility mode): PrintWindow is a synchronous
// cross-process call, not a cheap one, and this path only exists for the rare case where DWM can't
// do the job. Plain GDI throughout -- no GPU device, which is the whole point after the WGC removal.
internal sealed class ScreenshotCaptureSession : ITileCaptureSession
{
    private static readonly TimeSpan RecaptureInterval = TimeSpan.FromSeconds(1);
    private readonly nint _hwnd;
    private readonly Action<string>? _log;
    private DateTime _lastCaptureUtc = DateTime.MinValue;
    private DateTime _lastDirtyUtc = DateTime.MinValue;
    private Bitmap? _lastCapture;
    private bool _loggedFailure;

    // This session recaptures (via PrintWindow) only once a second, inside TryGetResizedFrame -- which
    // only runs when the pump decides to redraw. So drive the redraw ourselves on the same ~1fps
    // cadence, otherwise gating the pump on dirtiness would stop it from ever refreshing this tile.
    public bool ConsumeFrameDirty()
    {
        if (DateTime.UtcNow - _lastDirtyUtc < RecaptureInterval) return false;
        _lastDirtyUtc = DateTime.UtcNow;
        return true;
    }

    public ScreenshotCaptureSession(nint hwnd, Action<string>? log = null)
    {
        _hwnd = hwnd;
        _log = log;
    }

    public Bitmap? TryGetResizedFrame(int destWidth, int destHeight)
    {
        if (destWidth <= 0 || destHeight <= 0) return null;

        if (DateTime.UtcNow - _lastCaptureUtc >= RecaptureInterval)
        {
            _lastCaptureUtc = DateTime.UtcNow;
            var fresh = CaptureFrame();
            if (fresh is not null)
            {
                _lastCapture?.Dispose();
                _lastCapture = fresh;
            }
        }
        if (_lastCapture is null) return null;

        var resized = new Bitmap(destWidth, destHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(_lastCapture, new Rectangle(0, 0, destWidth, destHeight));
        }
        return resized;
    }

    private Bitmap? CaptureFrame()
    {
        try
        {
            if (!Utilities.Win32Native.GetWindowRect(_hwnd, out var rect)) return null;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return null;

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            bool ok;
            using (var g = Graphics.FromImage(bitmap))
            {
                var hdc = g.GetHdc();
                try { ok = Utilities.Win32Native.PrintWindow(_hwnd, hdc, Utilities.Win32Native.PwRenderFullContent); }
                finally { g.ReleaseHdc(hdc); }
            }
            if (!ok)
            {
                if (!_loggedFailure) { _log?.Invoke($"Screenshot fallback: PrintWindow failed for window {_hwnd}."); _loggedFailure = true; }
                bitmap.Dispose();
                return null;
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            if (!_loggedFailure) { _log?.Invoke($"Screenshot fallback capture failed for window {_hwnd}: {ex}"); _loggedFailure = true; }
            return null;
        }
    }

    public void Dispose()
    {
        _lastCapture?.Dispose();
        _lastCapture = null;
    }
}
