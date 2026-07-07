using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using EveDeck.Models;
using EveDeck.Views;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ── Frame overlay lifecycle ────────────────────────────────────────────────

    private void StartFrameOverlay()
    {
        _frameOverlay ??= new ActiveFrameOverlay();
        _frameTimer.Start();
    }

    private void StopFrameOverlay()
    {
        // The frame timer is shared with corner-overlay maintenance; only stop it when
        // corner overlays aren't relying on it.
        if (_cornerOverlays.Count == 0) _frameTimer.Stop();
        _frameOverlay?.Hide();
    }

    // 1b — Skip repositioning when handle and rect are unchanged.
    // 3a — Use per-slot color for the active window.
    private void OnFrameTick(object? sender, EventArgs e)
    {
        // Corner-overlay upkeep runs independently of the active-frame feature.
        MaintainCornerOverlays();

        if (!ActiveFrameEnabled || _frameOverlay is null)
        {
            _frameOverlay?.Hide();
            return;
        }

        var fgHandle = _windowService.GetForegroundWindowHandle();
        if (fgHandle == 0 || Windows.All(w => w.Handle != fgHandle))
        {
            if (_frameOverlay.IsVisible) _frameOverlay.Hide();
            if (_lastFrameHandle != 0)
            {
                _lastFrameHandle = 0;
                _lastFrameRect = null;
            }
            return;
        }

        if (!_windowService.TryGetWindowRect(fgHandle, out var rect))
        {
            if (_frameOverlay.IsVisible) _frameOverlay.Hide();
            return;
        }

        var handleChanged = _lastFrameHandle != fgHandle;
        var rectChanged = _lastFrameRect is null
            || _lastFrameRect.X != rect.X || _lastFrameRect.Y != rect.Y
            || _lastFrameRect.Width != rect.Width || _lastFrameRect.Height != rect.Height;

        if (handleChanged)
        {
            _lastFrameHandle = fgHandle;
        }

        if (handleChanged || rectChanged)
        {
            var brush = GetFrameBrushForWindow(fgHandle);
            if (!_frameOverlay.IsVisible) _frameOverlay.Show();
            _frameOverlay.ApplyFrame(rect.X, rect.Y, rect.Width, rect.Height, ActiveFrameThickness, ActiveFrameGlowRadius, brush);
            _lastFrameRect = rect;
        }
        else
        {
            // Rect/handle unchanged, so ApplyFrame (which re-asserts topmost) didn't run this tick.
            // Pinned clients / corner tiles may have been raised above the frame since the last apply,
            // so re-show if needed and re-raise it so it doesn't sink behind them -- kills the flicker.
            if (!_frameOverlay.IsVisible) _frameOverlay.Show();
            _frameOverlay.BringToTop();
        }
    }

    // 3a — Resolve frame color: per-slot if set, otherwise global.
    private Brush GetFrameBrushForWindow(nint handle)
    {
        var window = Windows.FirstOrDefault(w => w.Handle == handle);
        if (window is null) return _frameBrush;

        foreach (var assignment in Assignments)
        {
            if (!string.IsNullOrWhiteSpace(assignment.FrameColor)
                && assignment.AssignedWindows.Any(e => e.Title.Equals(window.Title, StringComparison.OrdinalIgnoreCase)))
            {
                return ParseFrameBrush(assignment.FrameColor);
            }
        }

        return _frameBrush;
    }
}
