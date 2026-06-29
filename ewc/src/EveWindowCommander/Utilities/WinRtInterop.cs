using System.Runtime.InteropServices;
using WinRT;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace EveWindowCommander.Utilities;

// Glue between the CsWinRT projections (Windows.Graphics.Capture / Direct3D11 interop) and the
// raw COM pointers that Vortice's D3D11 wrappers expose. All of this is the standard WGC interop
// boilerplate — there is no managed surface for these specific bridges.
internal static class WinRtInterop
{
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // True when the OS supports the WGC APIs we rely on (Windows 10 1903+).
    public static bool IsCaptureSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }

    // Build a GraphicsCaptureItem for a top-level window via the interop activation factory.
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
        var iid = IID_GraphicsCaptureItem;
        var itemPtr = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(itemPtr); }
        finally { Marshal.Release(itemPtr); }
    }

    // Wrap a DXGI device pointer (from Vortice's ID3D11Device) as the WinRT IDirect3DDevice that
    // Direct3D11CaptureFramePool requires.
    public static IDirect3DDevice CreateDirect3DDevice(IntPtr dxgiDevice)
    {
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var graphicsPtr);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        try { return MarshalInspectable<IDirect3DDevice>.FromAbi(graphicsPtr); }
        finally { Marshal.Release(graphicsPtr); }
    }

    // Pull the underlying ID3D11Texture2D pointer out of a captured frame's surface. The caller
    // owns the returned reference (wrap it in a Vortice ComObject, which releases on Dispose).
    public static IntPtr GetTexturePointer(IDirect3DSurface surface)
    {
        var unk = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(unk);
            var iid = IID_ID3D11Texture2D;
            return access.GetInterface(ref iid);
        }
        finally { Marshal.Release(unk); }
    }
}
