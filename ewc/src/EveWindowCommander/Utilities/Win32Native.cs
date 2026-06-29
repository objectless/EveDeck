using System.Runtime.InteropServices;

namespace EveWindowCommander.Utilities;

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

    // DWM thumbnail property flags
    internal const int DwmTnpRectDestination = 0x00000001;
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

    [DllImport("user32.dll")]
    internal static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

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
}
