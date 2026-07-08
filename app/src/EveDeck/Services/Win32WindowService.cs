using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using EveDeck.Models;

namespace EveDeck.Services;

public sealed class Win32WindowService
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsSysMenu = 0x00080000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int SwRestore = 9;
    private const long WsExLayered = 0x00080000L;
    private const uint LwaAlpha = 0x00000002;

    public IReadOnlyList<EveWindowInfo> FindEveWindows(bool includeNotepadTestWindows)
    {
        var windows = new List<EveWindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, 4) != IntPtr.Zero)
            {
                return true;
            }

            var title = GetWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var processId);
            Process? process = null;
            try { process = Process.GetProcessById((int)processId); }
            catch { return true; }

            var isEve = process.ProcessName.Equals("exefile", StringComparison.OrdinalIgnoreCase);
            var isNotepadTest = includeNotepadTestWindows && process.ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase);
            if (!isEve && !isNotepadTest)
            {
                return true;
            }

            if (!GetWindowRect(handle, out var rect))
            {
                return true;
            }

            windows.Add(new EveWindowInfo
            {
                Title = title,
                ProcessId = (int)processId,
                ProcessName = process.ProcessName,
                Handle = handle,
                Rect = new WindowRect
                {
                    X = rect.Left,
                    Y = rect.Top,
                    Width = rect.Right - rect.Left,
                    Height = rect.Bottom - rect.Top
                },
                MonitorId = GetMonitorDeviceName(MonitorFromWindow(handle, MonitorDefaultToNearest)),
                IsBorderless = IsLikelyBorderless(handle)
            });

            return true;
        }, IntPtr.Zero);

        return windows.OrderBy(w => w.Title).ThenBy(w => w.ProcessId).ToList();
    }

    // General-purpose lookup for a single visible top-level window belonging to a named process,
    // used by the Mumble utility overlay. Deliberately separate from FindEveWindows (which is
    // hard-filtered to the EVE client / notepad test windows) rather than widening that filter.
    // titleContains narrows to a specific window when a process owns more than one top-level window
    // (e.g. Mumble's main window vs. its separate "Talking UI" panel).
    public bool TryFindWindowByProcessName(string processName, out nint handle, out WindowRect rect, string? titleContains = null)
    {
        var found = (nint)0;
        var foundRect = new WindowRect();

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero)
            {
                return true;
            }

            var windowTitle = GetWindowTitle(h);
            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                return true;
            }

            if (titleContains is not null && windowTitle.IndexOf(titleContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return true;
            }

            GetWindowThreadProcessId(h, out var processId);
            Process? process = null;
            try { process = Process.GetProcessById((int)processId); }
            catch { return true; }

            if (!process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // A minimized window's GetWindowRect is a Windows-reported sentinel (roughly
            // -25000..-32000 with an icon-sized ~20x20 box), not a usable screen position -- if we
            // captured that as "original"/starting rect the target would end up permanently parked
            // off-screen (this bit Mumble in practice). Un-minimize first so the rect we read back
            // is real.
            if (IsIconic(h))
            {
                ShowWindow(h, SwRestore);
            }

            if (!GetWindowRect(h, out var nativeRect))
            {
                return true;
            }

            found = h;
            foundRect = ToWindowRect(nativeRect);
            return false; // first match is enough
        }, IntPtr.Zero);

        handle = found;
        rect = foundRect;
        return found != 0;
    }

    public bool IsProcessRunning(string processName)
    {
        try { return Process.GetProcessesByName(processName).Length > 0; }
        catch { return false; }
    }

    // Live DPI lookup for an arbitrary screen point, used by the utility overlay chrome to scale
    // its margins for wherever its target currently sits -- NOT the WPF window's own cached
    // OnSourceInitialized-time DPI, which can be wrong (e.g. captured while the chrome was still
    // sitting at its off-screen creation position, before being moved onto the target's monitor).
    public double GetDpiScaleForPoint(int x, int y)
    {
        var monitor = MonitorFromPoint(new PointNative { X = x, Y = y }, MonitorDefaultToNearest);
        var dpiX = 96u;
        var dpiY = 96u;
        try { GetDpiForMonitor(monitor, 0, out dpiX, out dpiY); } catch { }
        return dpiX / 96.0;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoNative();
            info.cbSize = Marshal.SizeOf<MonitorInfoNative>();
            if (GetMonitorInfo(monitor, ref info))
            {
                var dpiX = 96u;
                var dpiY = 96u;
                try { GetDpiForMonitor(monitor, 0, out dpiX, out dpiY); } catch { } // API absent pre-8.1, default DPI fallback

                monitors.Add(new MonitorInfo
                {
                    Id = info.szDevice,
                    DeviceName = info.szDevice,
                    IsPrimary = (info.dwFlags & 1) == 1,
                    Bounds = ToWindowRect(info.rcMonitor),
                    WorkArea = ToWindowRect(info.rcWork),
                    DpiX = dpiX,
                    DpiY = dpiY
                });
            }

            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    public bool IsWindowAlive(nint handle) => handle != 0 && IsWindow(handle);

    public void FocusWindow(nint handle)
    {
        if (handle == 0 || !IsWindow(handle))
        {
            throw new InvalidOperationException("Window handle is no longer valid.");
        }

        ShowWindow(handle, 9);
        if (!SetForegroundWindow(handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetForegroundWindow failed.");
        }
    }

    // Move two windows atomically in one compositor pass — position-only (SWP_NOSIZE) so
    // EVE does not see a resolution change and does not reset its UI layout.
    public void SwapWindowPositions(nint handle1, int x1, int y1, nint handle2, int x2, int y2)
    {
        var hdwp = BeginDeferWindowPos(2);
        if (hdwp == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "BeginDeferWindowPos failed.");

        hdwp = DeferWindowPos(hdwp, handle1, IntPtr.Zero, x1, y1, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        if (hdwp == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "DeferWindowPos failed for first window.");

        hdwp = DeferWindowPos(hdwp, handle2, IntPtr.Zero, x2, y2, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        if (hdwp == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "DeferWindowPos failed for second window.");

        if (!EndDeferWindowPos(hdwp)) throw new Win32Exception(Marshal.GetLastWin32Error(), "EndDeferWindowPos failed.");
    }

    public void MoveResizeWindow(nint handle, WindowRect rect)
    {
        if (handle == 0 || !IsWindow(handle))
        {
            throw new InvalidOperationException("Window handle is no longer valid.");
        }

        // Dangerous API: this moves/resizes another process window through the OS window manager only.
        // It does not send input, inject code, or inspect the target process.
        if (!SetWindowPos(handle, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, SwpNoZOrder | SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed while moving/resizing.");
        }
    }

    public StyleSnapshot CaptureStyle(nint handle, string title)
    {
        return new StyleSnapshot
        {
            WindowTitle = title,
            Style = GetWindowLongPtr(handle, GwlStyle).ToInt64(),
            ExStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64()
        };
    }

    public void MakeBorderless(nint handle)
    {
        if (handle == 0 || !IsWindow(handle))
        {
            throw new InvalidOperationException("Window handle is no longer valid.");
        }

        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        var borderlessStyle = style & ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu);

        // Dangerous API: this changes another process window style only at the OS window-manager level.
        // It never injects into, reads memory from, or sends gameplay input to the EVE client.
        SetWindowLongPtrChecked(handle, GwlStyle, new IntPtr(borderlessStyle), "SetWindowLongPtr(GWL_STYLE) failed.");
        ApplyFrameChanged(handle);
    }

    public void RestoreStyle(nint handle, StyleSnapshot snapshot)
    {
        if (handle == 0 || !IsWindow(handle))
        {
            throw new InvalidOperationException("Window handle is no longer valid.");
        }

        // Dangerous API: restore only the previously captured top-level window styles.
        SetWindowLongPtrChecked(handle, GwlStyle, new IntPtr(snapshot.Style), "SetWindowLongPtr(GWL_STYLE restore) failed.");
        SetWindowLongPtrChecked(handle, GwlExStyle, new IntPtr(snapshot.ExStyle), "SetWindowLongPtr(GWL_EXSTYLE restore) failed.");
        ApplyFrameChanged(handle);
    }

    public nint GetForegroundWindowHandle() => GetForegroundWindow();

    // Throttle or restore a process's OS scheduling priority. Open with minimum rights, operate, close.
    // Dangerous API: changes OS CPU scheduling for another process; does not inject code or send input.
    public bool SetProcessPriority(uint pid, bool background)
    {
        var handle = Utilities.Win32Native.OpenProcess(Utilities.Win32Native.ProcessSetInformation, false, pid);
        if (handle == 0) return false;
        try
        {
            return Utilities.Win32Native.SetPriorityClass(handle,
                background ? Utilities.Win32Native.PriorityBelowNormal : Utilities.Win32Native.PriorityNormal);
        }
        finally { Utilities.Win32Native.CloseHandle(handle); }
    }

    // Set or clear HWND_TOPMOST on a window via SWP_NOMOVE|SWP_NOSIZE.
    // Dangerous API: modifies Z-order of another process window at OS level only.
    public void SetWindowTopmost(nint handle, bool topmost)
    {
        if (handle == 0 || !IsWindow(handle)) return;
        var insertAfter = topmost
            ? (nint)Utilities.Win32Native.HwndTopmost
            : (nint)Utilities.Win32Native.HwndNotTopmost;
        SetWindowPos(handle, insertAfter, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    // 3c — Set WS_EX_LAYERED + LWA_ALPHA to give a window partial transparency.
    public void SetWindowOpacity(nint handle, int percentOpacity)
    {
        if (handle == 0 || !IsWindow(handle)) return;
        var exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        if ((exStyle & WsExLayered) == 0)
            SetWindowLongPtrChecked(handle, GwlExStyle, new IntPtr(exStyle | WsExLayered), "SetWindowLongPtr(WS_EX_LAYERED) failed.");
        var alpha = (byte)Math.Clamp(percentOpacity * 255 / 100, 0, 255);
        SetLayeredWindowAttributes(handle, 0, alpha, LwaAlpha);
    }

    // 3c — Remove WS_EX_LAYERED (restore full opacity).
    public void RemoveOpacity(nint handle)
    {
        if (handle == 0 || !IsWindow(handle)) return;
        var exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        if ((exStyle & WsExLayered) == 0) return;
        // WS_EX_LAYERED is transparency-only; it does not affect non-client area geometry,
        // so no SWP_FRAMECHANGED is needed (which would send WM_NCCALCSIZE and allow the
        // game window to reposition itself).
        SetWindowLongPtrChecked(handle, GwlExStyle, new IntPtr(exStyle & ~WsExLayered), "RemoveOpacity SetWindowLongPtr failed.");
    }

    public bool TryGetWindowRect(nint handle, out WindowRect rect)
    {
        if (handle != 0 && IsWindow(handle) && GetWindowRect(handle, out var native))
        {
            rect = ToWindowRect(native);
            return true;
        }
        rect = new WindowRect();
        return false;
    }

    private static bool IsLikelyBorderless(nint handle)
    {
        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        return (style & WsCaption) == 0 && (style & WsThickFrame) == 0;
    }

    private static void ApplyFrameChanged(nint handle)
    {
        if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos frame refresh failed.");
        }
    }

    private static void SetWindowLongPtrChecked(nint handle, int index, IntPtr value, string message)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(handle, index, value);
        var error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0)
        {
            throw new Win32Exception(error, message);
        }
    }

    private static string GetWindowTitle(nint handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetMonitorDeviceName(IntPtr monitor)
    {
        var info = new MonitorInfoNative();
        info.cbSize = Marshal.SizeOf<MonitorInfoNative>();
        return GetMonitorInfo(monitor, ref info) ? info.szDevice : "";
    }

    private static WindowRect ToWindowRect(RectNative rect)
        => new() { X = rect.Left, Y = rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top };

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectNative lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointNative pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoNative lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr BeginDeferWindowPos(int nNumWindows);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoNative
    {
        public int cbSize;
        public RectNative rcMonitor;
        public RectNative rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
