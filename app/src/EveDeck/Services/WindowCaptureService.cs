using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace EveDeck.Services;

// Bridges Windows.Graphics.Capture (WGC) into Vortice Direct3D11/Direct2D1 so corner-tile previews
// can be real per-frame GPU captures with controllable, high-quality resizing -- instead of relying
// solely on DwmRegisterThumbnail (TileSurfaceWindow's existing/fallback path), which has no filter
// control and looks soft at small tile sizes or when magnified by Zoom hover.
//
// IMPORTANT (see memory project-overlay-single-surface): the 2026-07-07 rewrite that killed the
// historical preview/label flicker did so by consolidating ~10 per-tile top-level windows into ONE
// TileSurfaceWindow with event-driven z-order. That fix was about window COUNT, not DWM vs WGC as a
// capture technology. So captured frames here are pulled by TileSurfaceWindow and blitted into its
// own composited bitmap (see Redraw) -- never drawn via a separate per-tile window -- which keeps
// that architecture intact.
//
// One shared device pair for the whole app (this class); one CaptureSession per live tile. If GPU/
// driver support is missing (rare -- old hardware/driver), TryCreate returns null and every tile
// falls back to the existing DwmRegisterThumbnail path.
internal sealed class WindowCaptureService : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly ID2D1Device _d2dDevice;
    private readonly object _gpuLock = new(); // ID3D11DeviceContext/ID2D1DeviceContext are not thread-safe; every GPU op funnels through this.
    private bool _disposed;

    private WindowCaptureService(ID3D11Device device, IDirect3DDevice winrtDevice, ID2D1Device d2dDevice)
    {
        _device = device;
        _winrtDevice = winrtDevice;
        _d2dDevice = d2dDevice;
    }

    public static WindowCaptureService? TryCreate(Action<string>? log)
    {
        try
        {
            log?.Invoke($"WGC diagnostic: GraphicsCaptureSession.IsSupported() = {GraphicsCaptureSession.IsSupported()}");
            var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, null!);
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            var winrtDevice = WgcInterop.CreateDirect3DDeviceFromDXGIDevice(dxgiDevice);
            var d2dDevice = D2D1.D2D1CreateDevice(dxgiDevice, null);
            return new WindowCaptureService(device, winrtDevice, d2dDevice);
        }
        catch (Exception ex)
        {
            // Full ToString(), not just Message: this is a rare, one-time-at-startup path, and a
            // bare exception message alone (e.g. "Specified cast is not valid") isn't enough to
            // diagnose a real interop bug after the fact -- see the CreateSession catch below for
            // the same reasoning, which is what this exact gap cost during live debugging.
            log?.Invoke($"WGC capture unavailable, falling back to DWM thumbnails: {ex}");
            return null;
        }
    }

    // Null when the window can't be captured (closed between liveness check and here, or an
    // unsupported/protected window) -- caller falls back to DwmRegisterThumbnail for that tile.
    public CaptureSession? CreateSession(nint hwnd, Action<string>? log)
    {
        try
        {
            var item = WgcInterop.CreateItemForWindow(hwnd);
            return new CaptureSession(this, item);
        }
        catch (Exception ex)
        {
            // Full ToString() (type + message + stack trace), not just Message -- a bare "Specified
            // cast is not valid" with no stack trace is nearly undiagnosable after the fact.
            log?.Invoke($"WGC capture session failed for window {hwnd}: {ex}");
            return null;
        }
    }

    internal IDirect3DDevice WinRtDevice => _winrtDevice;

    // Draws `source` (the latest captured frame) onto a destWidth x destHeight D2D render target with
    // high-quality interpolation, then reads back just that small surface to a GDI+ Bitmap. Bounding
    // the CPU readback to the DESTINATION tile size (not the source window's native resolution) is
    // the key perf mitigation vs. naive full-frame readback.
    internal Bitmap? ResizeToBitmap(ID3D11Texture2D source, int destWidth, int destHeight, ref TileRenderTarget? cache)
    {
        if (destWidth <= 0 || destHeight <= 0) return null;

        lock (_gpuLock)
        {
            try
            {
                cache = TileRenderTarget.EnsureSize(_device, _d2dDevice, cache, destWidth, destHeight);
                var target = cache!;

                using var sourceSurface = source.QueryInterface<IDXGISurface>();
                using var sourceBitmap = target.DeviceContext.CreateBitmapFromDxgiSurface(sourceSurface, null);

                target.DeviceContext.BeginDraw();
                target.DeviceContext.Clear(null);
                target.DeviceContext.DrawBitmap(
                    sourceBitmap,
                    (Vortice.RawRectF?)new Vortice.RawRectF(0, 0, destWidth, destHeight),
                    1.0f,
                    Vortice.Direct2D1.InterpolationMode.HighQualityCubic,
                    (Vortice.RawRectF?)null,
                    (System.Numerics.Matrix4x4?)null);
                target.DeviceContext.EndDraw();

                _device.ImmediateContext.CopyResource(target.Staging, target.RenderTexture);

                var mapped = _device.ImmediateContext.Map(target.Staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var bitmap = new Bitmap(destWidth, destHeight, PixelFormat.Format32bppArgb);
                    var bits = bitmap.LockBits(new Rectangle(0, 0, destWidth, destHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        var rowBytes = destWidth * 4;
                        for (var y = 0; y < destHeight; y++)
                        {
                            var srcRow = mapped.DataPointer + y * mapped.RowPitch;
                            var dstRow = bits.Scan0 + y * bits.Stride;
                            unsafe { Buffer.MemoryCopy((void*)srcRow, (void*)dstRow, bits.Stride, rowBytes); }
                        }
                    }
                    finally { bitmap.UnlockBits(bits); }
                    return bitmap;
                }
                finally { _device.ImmediateContext.Unmap(target.Staging, 0); }
            }
            catch
            {
                cache?.Dispose();
                cache = null;
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _d2dDevice.Dispose();
        _winrtDevice.Dispose();
        _device.Dispose();
    }
}

// Per-tile capture: one GraphicsCaptureItem/FramePool/Session per live corner-tile source window.
// FrameArrived callbacks land on WGC's own capture thread -- this class only ever atomically swaps a
// "latest frame" reference there; all GPU/CPU work (resize + readback) happens on the caller's thread
// (TileSurfaceWindow's pump timer) via WindowCaptureService.ResizeToBitmap, serialized by _gpuLock.
internal sealed class CaptureSession : IDisposable
{
    private readonly WindowCaptureService _owner;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _frameLock = new();
    private ID3D11Texture2D? _latestFrame;
    private TileRenderTarget? _renderTarget;
    private bool _disposed;

    internal CaptureSession(WindowCaptureService owner, GraphicsCaptureItem item)
    {
        _owner = owner;
        _item = item;

        var size = item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            owner.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        ID3D11Texture2D texture;
        try { texture = WgcInterop.TextureFromSurface(frame.Surface); }
        catch { return; } // source window closed mid-capture, etc. -- keep the previous frame

        lock (_frameLock)
        {
            // Dispose() unsubscribes FrameArrived before tearing down, but an invocation already in
            // flight on the capture thread can still land here concurrently -- drop it instead of
            // resurrecting _latestFrame after Dispose has cleared it (that texture would then never
            // get released, since nothing will call TryGetResizedFrame for this session again).
            if (_disposed) { texture.Dispose(); return; }
            _latestFrame?.Dispose();
            _latestFrame = texture;
        }
    }

    // Pulls the latest captured frame resized to destWidth x destHeight. Null if no frame has
    // arrived yet (session just started) or the resize/readback failed.
    public Bitmap? TryGetResizedFrame(int destWidth, int destHeight)
    {
        ID3D11Texture2D? frame;
        lock (_frameLock) { frame = _latestFrame; }
        return frame is null ? null : _owner.ResizeToBitmap(frame, destWidth, destHeight, ref _renderTarget);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _framePool.FrameArrived -= OnFrameArrived;
        try { _session.Dispose(); } catch { }
        try { _framePool.Dispose(); } catch { }
        lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; }
        _renderTarget?.Dispose();
        _renderTarget = null;
    }
}

// The small per-tile GPU render target + CPU staging texture used to resize a captured frame down to
// its destination tile size before readback. Recreated only when the destination size actually
// changes (tile resize, zoom in/out) -- not every frame.
internal sealed class TileRenderTarget : IDisposable
{
    public required ID3D11Texture2D RenderTexture { get; init; }
    public required ID3D11Texture2D Staging { get; init; }
    public required ID2D1DeviceContext DeviceContext { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public static TileRenderTarget EnsureSize(ID3D11Device device, ID2D1Device d2dDevice, TileRenderTarget? existing, int width, int height)
    {
        if (existing is not null && existing.Width == width && existing.Height == height) return existing;
        existing?.Dispose();

        var renderTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });

        var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        using var dxgiSurface = renderTexture.QueryInterface<IDXGISurface>();
        var deviceContext = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        var targetBitmap = deviceContext.CreateBitmapFromDxgiSurface(dxgiSurface, new BitmapProperties1
        {
            BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw,
            PixelFormat = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
        });
        deviceContext.Target = targetBitmap;

        return new TileRenderTarget
        {
            RenderTexture = renderTexture,
            Staging = staging,
            DeviceContext = deviceContext,
            Width = width,
            Height = height,
        };
    }

    public void Dispose()
    {
        DeviceContext.Target = null;
        DeviceContext.Dispose();
        Staging.Dispose();
        RenderTexture.Dispose();
    }
}

internal static class WgcInterop
{
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    // The real WinRT interface IID for Windows.Graphics.Capture.IGraphicsCaptureItem, used as the
    // riid CreateForWindow queries the new item for -- NOT typeof(GraphicsCaptureItem).GUID (see the
    // long comment on CreateItemForWindow below for why that reflection call is the wrong source).
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DirectDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Id3D11Texture2DGuid = typeof(ID3D11Texture2D).GUID;

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true, PreserveSig = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr runtimeClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowDelegate(IntPtr thisPtr, IntPtr window, ref Guid riid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceDelegate(IntPtr thisPtr, ref Guid riid, out IntPtr result);

    // FIXED (found live, v1.20.0), 4th pass -- the real fix. Attempts 1-3 (RCW cast, manual QI,
    // single-shot ActivationFactory.Get(typeName, interopIid)) all chased the FACTORY-acquisition
    // IID and all reported E_NOINTERFACE, so all three assumed the interop factory itself was the
    // problem. That assumption was wrong. A raw combase.dll RoGetActivationFactory P/Invoke, added
    // as a diagnostic probe planted directly in this exact call path, proved the factory acquisition
    // ALWAYS succeeds (S_OK) -- the E_NOINTERFACE was actually coming from the CreateForWindow method
    // call itself, on its `riid` out-parameter. That riid was `typeof(GraphicsCaptureItem).GUID`,
    // which for a CsWinRT-projected CLASS (not interface) is a .NET-synthesized pseudo-GUID with no
    // relationship to the real WinRT interface IID -- confirmed live: it evaluates to
    // cc7b16ab-e4bc-3d0e-a4eb-4fdb9ce0a1ff, nothing like the documented IGraphicsCaptureItem IID
    // 79c3f95b-31f7-4ec2-a464-632ef5d30760 (which Microsoft's own WPF capture sample hardcodes as a
    // constant for exactly this reason, rather than reflecting it). So the actual OS activation
    // factory was always fine; we were just asking CreateForWindow to hand back an interface under
    // the wrong IID and it correctly said no such interface. Fixed by using the real hardcoded IID
    // (GraphicsCaptureItemIid above) instead of reflection.
    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var hstr));
        IntPtr interopPtr;
        try
        {
            var interopIid = GraphicsCaptureItemInteropIid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstr, ref interopIid, out interopPtr));
        }
        finally { WindowsDeleteString(hstr); }

        try
        {
            var vtbl = Marshal.ReadIntPtr(interopPtr);
            var createForWindow = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(
                Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));

            var itemIid = GraphicsCaptureItemIid;
            Marshal.ThrowExceptionForHR(createForWindow(interopPtr, hwnd, ref itemIid, out var itemPtr));
            // Ownership: MarshalInterface/GraphicsCaptureItem.FromAbi does its own internal
            // QueryInterface/AddRef via ComWrappers -- it does NOT consume itemPtr's reference, so we
            // still release it ourselves afterward (verified against the exact CsWinRT source matching
            // this app's referenced WinRT.Runtime version).
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally { Marshal.Release(interopPtr); }
    }

    public static IDirect3DDevice CreateDirect3DDeviceFromDXGIDevice(IDXGIDevice dxgiDevice)
    {
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var deviceHandle);
        Marshal.ThrowExceptionForHR(hr);
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(deviceHandle); }
        finally { Marshal.Release(deviceHandle); }
    }

    // Same vtable-direct technique as CreateItemForWindow, and the same reasoning for why: this went
    // through the identical AsInterface<T>()-on-an-RCW path and would very likely hit the same bug
    // once real frames started flowing (never actually reached live, since no capture session got
    // this far before the CreateItemForWindow fix).
    public static ID3D11Texture2D TextureFromSurface(IDirect3DSurface surface)
    {
        var objRef = ((IWinRTObject)surface).NativeObject;
        var accessIid = DirectDxgiInterfaceAccessIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(objRef.ThisPtr, in accessIid, out var accessPtr));
        try
        {
            var vtbl = Marshal.ReadIntPtr(accessPtr);
            var getInterface = Marshal.GetDelegateForFunctionPointer<GetInterfaceDelegate>(
                Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));

            var texIid = Id3D11Texture2DGuid;
            Marshal.ThrowExceptionForHR(getInterface(accessPtr, ref texIid, out var texturePtr));
            // Different ownership rule here, deliberately NOT releasing texturePtr: Vortice's
            // ComObject(IntPtr) constructor (unlike FromAbi above) ADOPTS the pointer's existing
            // reference as-is -- no internal AddRef -- and Dispose()/finalization calls Release()
            // exactly once for it (verified against SharpGen.Runtime's ComObject/CppObject source).
            // Releasing texturePtr here too would be a double-release against whatever
            // CaptureSession's Dispose() does to the returned ID3D11Texture2D later.
            return new ID3D11Texture2D(texturePtr);
        }
        finally { Marshal.Release(accessPtr); }
    }
}
