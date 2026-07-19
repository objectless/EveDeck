using System.Runtime.InteropServices;

namespace EveDeck.Utilities;

internal static class Win32Native
{
    internal const int GwlExStyle = -16; // GWL_STYLE is -16; GWL_EXSTYLE is -20
    internal const int GwlExStyleIndex = -20;
    internal const nint WsExTransparent = 0x00000020;
    internal const long WsExLayered = 0x00080000L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint LwaAlpha = 0x00000002;
    internal const nint HwndBottom = 1;
    internal const nint HwndTop = 0;
    internal const nint HwndTopmost = -1;
    internal const nint HwndNotTopmost = -2;

    internal const int WmMouseMove = 0x0200;
    internal const int WmMouseLeave = 0x02A3;
    internal const uint TmeLeave = 0x00000002;

    // SetWindowLongPtr index for the owner window. Setting an owner makes the window manager keep
    // this window above its owner in the z-order permanently -- the mechanism the label surface
    // uses to stay above the tile surface without any per-tick SetWindowPos maintenance.
    internal const int GwlpHwndParent = -8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct TrackMouseEventStruct
    {
        public uint cbSize;
        public uint dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [DllImport("user32.dll")]
    internal static extern bool TrackMouseEvent(ref TrackMouseEventStruct lpEventTrack);

    [DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string lpString);

    // DWM thumbnail property flags
    internal const int DwmTnpRectDestination = 0x00000001;
    internal const int DwmTnpOpacity = 0x00000004;
    internal const int DwmTnpVisible = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DwmThumbnailProperties
    {
        public int dwFlags;
        public NativeRect rcDestination;
        public NativeRect rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    internal static extern nint GetWindowLongPtr(nint hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    internal static extern nint SetWindowLongPtr(nint hwnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(nint hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);

    // PW_RENDERFULLCONTENT (0x2, Windows 8.1+) is required for hardware-accelerated (DirectX/OpenGL)
    // content like the EVE client -- without it PrintWindow captures a blank/black frame for
    // GPU-rendered windows. Used only as a last-resort capture path when both WGC and DWM
    // thumbnails fail for a window (e.g. some RDP/remote-desktop or virtual-display setups).
    internal const uint PwRenderFullContent = 0x2;

    [DllImport("user32.dll")]
    internal static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    internal static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    // Per-pixel alpha layered-window rendering (UpdateLayeredWindow), used by TileSurfaceWindow so
    // the backplate itself carries real alpha instead of the binary color-key transparency that
    // SetLayeredWindowAttributes/TransparencyKey provides.
    internal const uint UlwAlpha = 0x00000002;
    internal const byte AcSrcOver = 0x00;
    internal const byte AcSrcAlpha = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SizeNative { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    internal static extern bool UpdateLayeredWindow(nint hwnd, nint hdcDst, ref NativePoint pptDst, ref SizeNative psize,
        nint hdcSrc, ref NativePoint pptSrc, uint crKey, ref BlendFunction pblend, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint hDC);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(nint hDC);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint hDC, nint hObject);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool SetWindowText(nint hWnd, string lpString);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmRegisterThumbnail(nint hwndDestination, nint hwndSource, out nint phThumbnailId);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmUpdateThumbnailProperties(nint hThumbnailId, ref DwmThumbnailProperties ptnProperties);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmUnregisterThumbnail(nint hThumbnailId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out NativePoint lpPoint);

    // Display mode enumeration (for VSR/DSR resolution picker).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        // Union: display variant (POINTL dmPosition + 2x DWORD)
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        // Post-union shared fields
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplaySettingsEx(
        string? lpszDeviceName,
        uint iModeNum,
        ref DEVMODE lpDevMode,
        uint dwFlags);

    // Process priority constants for background throttling.
    internal const uint ProcessSetInformation = 0x0200;
    internal const uint PriorityNormal = 0x00000020;
    internal const uint PriorityBelowNormal = 0x00004000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPriorityClass(nint hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint hObject);

    // EveDeck's own opt-out from Windows' background-process power throttling (EcoQoS). Its main
    // window is normally hidden to tray (see MainWindow.xaml.cs's HideToTray) and its overlay
    // surfaces are WS_EX_TOOLWINDOW (deliberately excluded from Alt+Tab/taskbar so they don't count
    // as a "real" app window either) -- Task Manager buckets a process with no such window under
    // "Background processes", which Windows can then throttle (EcoQoS/lower scheduling priority).
    // For most background work that's fine or even desirable, but EveDeck's whole job while hidden
    // is time-sensitive Win32 window management (topmost reassertion, hover-peek swaps) -- being
    // throttled shows up as z-order lag (a hover-zoomed tile briefly rendering under the master
    // window) and occasional stutter. This explicitly disables EXECUTION_SPEED throttling for this
    // process regardless of its Task Manager classification, matching the documented pattern in
    // H.NotifyIcon (a widely-used WPF tray-icon library) and Microsoft's own EcoQoS devblog post.
    internal const uint ProcessPowerThrottlingCurrentVersion = 1;
    internal const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    internal const int ProcessInformationClassProcessPowerThrottling = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    internal static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool SetProcessInformation(
        nint hProcess, int processInformationClass, void* processInformation, uint processInformationSize);

    internal static unsafe void DisableOwnProcessPowerThrottling()
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = ProcessPowerThrottlingExecutionSpeed,
            StateMask = 0, // 0 = explicitly OFF for the mechanism selected by ControlMask
        };
        SetProcessInformation(
            GetCurrentProcess(),
            ProcessInformationClassProcessPowerThrottling,
            &state,
            (uint)sizeof(ProcessPowerThrottlingState));
    }
}
