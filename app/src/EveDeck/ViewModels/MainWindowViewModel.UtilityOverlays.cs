using EveDeck.Models;
using EveDeck.Views;

namespace EveDeck.ViewModels;

// Free-form, always-on-top Mumble "Talking UI" overlay: EveDeck moves the user's real,
// already-running window rather than reimplementing Mumble's client (see project chat history/plan
// for why -- a native Mumble protocol client was explicitly ruled out, since detecting "talking"
// requires a second bot connection visible to everyone else in the channel). Fixed cardinality of 1,
// so this mirrors StartFrameOverlay/StopFrameOverlay's lazy-create shape (MainWindowViewModel.Overlay.cs)
// rather than the corner-overlay's per-tick teardown-and-rebuild shape.
public sealed partial class MainWindowViewModel
{
    private UtilityOverlayChrome? _mumbleChrome;

    public bool MumbleOverlayEnabled
    {
        get => _settings.MumbleOverlay.Enabled;
        set
        {
            if (_settings.MumbleOverlay.Enabled == value) return;
            _settings.MumbleOverlay.Enabled = value;
            OnPropertyChanged();
            if (value)
            {
                _mumbleChrome = AttachUtilityOverlay(_settings.MumbleOverlay, "mumble", MumbleTalkingUiTitle, _mumbleChrome, "Mumble", DetachMumbleFromChrome);
                if (_mumbleChrome is not null) _mumbleOverlayRepositionTimer.Start();
            }
            else
            {
                DetachUtilityOverlay(_settings.MumbleOverlay, _mumbleChrome);
                _mumbleChrome = null;
                _mumbleOverlayRepositionTimer.Stop();
            }
            OnPropertyChanged(nameof(MumbleOverlayStatus));
            Save();
        }
    }

    // Slider-bound; applies live to the attached window (and its chrome) and persists. The 20%
    // floor mirrors UtilityOverlayChrome.ApplyOpacity's clamp so a mostly-invisible overlay can't
    // be created in the first place.
    public int MumbleOverlayOpacity
    {
        get => _settings.MumbleOverlay.OpacityPercent;
        set
        {
            var clamped = Math.Clamp(value, 20, 100);
            if (_settings.MumbleOverlay.OpacityPercent == clamped) return;
            _settings.MumbleOverlay.OpacityPercent = clamped;
            OnPropertyChanged();
            _mumbleChrome?.ApplyOpacity();
            Save();
        }
    }

    // Mumble ships a purpose-built "Talking UI" panel (Settings -> User Interface -> Talking UI) --
    // a small always-on-top window listing who's currently speaking. We wrap that instead of
    // Mumble's main window so the overlay is a talking-status HUD, not a full chat client window.
    private const string MumbleTalkingUiTitle = "Talking UI";

    public string MumbleOverlayStatus => _mumbleChrome is not null
        ? "Attached to Mumble's Talking UI"
        : _settings.MumbleOverlay.Enabled
            ? _windowService.IsProcessRunning("mumble")
                ? "Mumble is running — enable Talking UI in Mumble's Settings > User Interface"
                : "Mumble not found — is it running?"
            : "Disabled";

    // The chrome's own Detach button routes back through the Enabled setter so restore + Save +
    // status all go through the one code path above instead of being duplicated here.
    private void DetachMumbleFromChrome() => MumbleOverlayEnabled = false;

    // Called from Refresh() (so it also benefits from the existing 5s AutoRefresh timer) to retry
    // attaching when Mumble wasn't running yet, and to detect it closing so we can clean up
    // gracefully instead of erroring against a dead handle. Position/size re-assertion itself runs
    // on a faster dedicated timer (see MaintainMumbleOverlayPosition) so drift from Mumble's own
    // auto-resizing doesn't take up to 5s to correct.
    private void MaintainUtilityOverlays()
    {
        if (_settings.MumbleOverlay.Enabled)
        {
            if (_mumbleChrome is not null && !_mumbleChrome.IsTargetAlive())
            {
                _mumbleChrome.Close();
                _mumbleChrome = null;
                _settings.MumbleOverlay.OriginalRect = null; // target is gone -- nothing left to restore
                _mumbleOverlayRepositionTimer.Stop();
                OnPropertyChanged(nameof(MumbleOverlayStatus));
            }
            if (_mumbleChrome is null)
            {
                _mumbleChrome = AttachUtilityOverlay(_settings.MumbleOverlay, "mumble", MumbleTalkingUiTitle, _mumbleChrome, "Mumble", DetachMumbleFromChrome);
                if (_mumbleChrome is not null) _mumbleOverlayRepositionTimer.Start();
                OnPropertyChanged(nameof(MumbleOverlayStatus));
            }
        }
    }

    // Some target apps fight back against our forced geometry after the fact -- Mumble's Talking UI
    // especially, since it auto-resizes to fit its current speaker roster and can hide() itself when
    // momentarily empty. Re-assert our slot rect (which also re-applies SWP_SHOWWINDOW) on a short,
    // dedicated cadence (see _mumbleOverlayRepositionTimer in MainWindowViewModel.cs) rather than
    // piggybacking on the heavier 5s Refresh()/MaintainUtilityOverlays() reconciliation, so drift
    // self-heals within about a second instead of up to five.
    private void MaintainMumbleOverlayPosition()
    {
        if (_mumbleChrome is not null && _mumbleChrome.IsTargetAlive())
            _mumbleChrome.Reposition();
    }

    private UtilityOverlayChrome? AttachUtilityOverlay(UtilityOverlaySlot slot, string processName,
        string? windowTitleFilter, UtilityOverlayChrome? chrome, string title, Action detachCallback)
    {
        if (!_windowService.TryFindWindowByProcessName(processName, out var handle, out var liveRect, windowTitleFilter))
            return chrome;

        // A stale OriginalRect/target position can persist across app restarts if EveDeck was
        // force-killed instead of exited cleanly (skips the restore-on-exit path -- see memory),
        // and either could have been captured while the target was minimized (Windows reports a
        // ~-25000..-32000, icon-sized rect for GetWindowRect on a minimized window, not a usable
        // position -- this bit Mumble in practice). Re-derive from the live rect whenever the
        // saved value looks like one of those cases rather than trusting it blindly.
        if (slot.OriginalRect is null || IsLikelySentinelRect(slot.OriginalRect))
        {
            slot.OriginalRect = liveRect;
            var snap = _windowService.CaptureStyle(handle, title);
            slot.OriginalStyle = snap.Style;
            slot.OriginalExStyle = snap.ExStyle;
        }

        if ((slot.X == 0 && slot.Y == 0) || IsLikelySentinelRect(new WindowRect { X = slot.X, Y = slot.Y, Width = slot.Width, Height = slot.Height }))
            (slot.X, slot.Y, slot.Width, slot.Height) = (liveRect.X, liveRect.Y, liveRect.Width, liveRect.Height);

        _windowService.MakeBorderless(handle);

        if (chrome is null)
        {
            chrome = new UtilityOverlayChrome(title);
            chrome.DetachRequested += detachCallback;
        }
        chrome.Attach(handle, slot, _windowService, Save);
        chrome.Show();
        Log.Info($"{title} overlay attached.");
        return chrome;
    }

    // A minimized window's GetWindowRect is a Windows placeholder, never a real screen position --
    // and separately, Mumble's own Talking UI can park itself off-screen at a small size when its
    // speaker roster is empty (if "always show" isn't enabled in Mumble's own settings). Observed
    // real examples: a ~20x20 icon-sized box, but also a ~128x22 and ~160x28 box -- so this only
    // checks the coordinate, not size, since no real monitor layout in this app's supported range
    // extends anywhere near -10000 (see UtilityOverlayChrome.IsLikelySentinelRect, which uses the
    // same check to avoid permanently re-forcing a bad rect once one gets persisted).
    private static bool IsLikelySentinelRect(WindowRect rect) =>
        rect.X < -10000 || rect.Y < -10000;

    private void DetachUtilityOverlay(UtilityOverlaySlot slot, UtilityOverlayChrome? chrome)
    {
        var handle = chrome?.TargetHandle ?? 0;
        if (slot.OriginalRect is { } original && handle != 0 && _windowService.IsWindowAlive(handle))
        {
            try
            {
                // RestoreStyle re-applies the captured ex-style, but the captured value could itself
                // already include WS_EX_LAYERED with our alpha attribute still set -- explicitly drop
                // our opacity first so the user's normal window never comes back translucent.
                _windowService.RemoveOpacity(handle);
                _windowService.RestoreStyle(handle, new StyleSnapshot { Style = slot.OriginalStyle, ExStyle = slot.OriginalExStyle });
                _windowService.MoveResizeWindow(handle, original);
            }
            catch (Exception ex) { Log.Warn($"Could not restore original window state: {ex.Message}"); }
        }
        chrome?.Close();
        slot.OriginalRect = null;
    }

    // Called from Cleanup() on app exit -- the concrete fix for "restore the user's normal Mumble
    // window on EveDeck exit."
    private void DetachAllUtilityOverlaysOnExit()
    {
        if (_mumbleChrome is not null) DetachUtilityOverlay(_settings.MumbleOverlay, _mumbleChrome);
        _mumbleChrome = null;
    }
}
