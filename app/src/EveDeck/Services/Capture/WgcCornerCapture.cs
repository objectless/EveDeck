using System.Numerics;
using EveDeck.Utilities;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using D3D11 = Vortice.Direct3D11.D3D11;

namespace EveDeck.Services.Capture;

// Captures a single source window via Windows.Graphics.Capture and renders a high-quality,
// trilinear-minified copy into a child HWND's DXGI swap chain. Each instance owns its own D3D11
// device so frame callbacks (raised on a free-threaded pool) never touch a shared context.
//
// Quality vs. DWM thumbnails: the captured frame is copied into a full mip-chain texture, mips are
// generated on the GPU, and a full-screen triangle samples it with a MinMagMipLinear sampler. That
// area-averaged (trilinear) minification is dramatically less aliased than DWM's single bilinear tap.
internal sealed class WgcCornerCapture : IDisposable
{
    private const string ShaderSource = @"
Texture2D tex : register(t0);
SamplerState smp : register(s0);
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv  = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float4 PSMain(VSOut i) : SV_TARGET { return tex.Sample(smp, i.uv); }";

    private readonly object _lock = new();
    private readonly int _destWidth;
    private readonly int _destHeight;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1 _swapChain = null!;
    private ID3D11RenderTargetView _backBufferRtv = null!;
    private ID3D11VertexShader _vs = null!;
    private ID3D11PixelShader _ps = null!;
    private ID3D11SamplerState _sampler = null!;

    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipSrv;
    private int _mipWidth, _mipHeight;

    private IDirect3DDevice _winrtDevice = null!;
    private GraphicsCaptureItem _item = null!;
    private Direct3D11CaptureFramePool _framePool = null!;
    private GraphicsCaptureSession _session = null!;
    private SizeInt32 _lastContentSize;
    private bool _disposed;

    public WgcCornerCapture(int destWidth, int destHeight)
    {
        _destWidth = Math.Max(1, destWidth);
        _destHeight = Math.Max(1, destHeight);
    }

    // Initialise device/swap chain/capture. Throws on failure; the caller falls back to DWM.
    public void Start(nint sourceHwnd, nint targetHwnd)
    {
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out _device!, out _context!).CheckError();

        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)_destWidth,
                Height = (uint)_destHeight,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
                Flags = SwapChainFlags.None
            };
            _swapChain = factory.CreateSwapChainForHwnd(_device, targetHwnd, desc);
            factory.MakeWindowAssociation(targetHwnd, WindowAssociationFlags.IgnoreAltEnter);

            _winrtDevice = WinRtInterop.CreateDirect3DDevice(dxgiDevice.NativePointer);
        }

        using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            _backBufferRtv = _device.CreateRenderTargetView(backBuffer);

        var vsBlob = Compiler.Compile(ShaderSource, "VSMain", "corner.hlsl", "vs_5_0");
        var psBlob = Compiler.Compile(ShaderSource, "PSMain", "corner.hlsl", "ps_5_0");
        _vs = _device.CreateVertexShader(vsBlob.Span);
        _ps = _device.CreatePixelShader(psBlob.Span);

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });

        _item = WinRtInterop.CreateItemForWindow(sourceHwnd);
        _lastContentSize = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_item);
        TryDisableBorderAndCursor();
        _session.StartCapture();
    }

    // Repoint at a different source window (after a swap) without tearing down the swap chain.
    public void SetSource(nint sourceHwnd)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                _session?.Dispose();
                if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
                _framePool?.Dispose();

                _item = WinRtInterop.CreateItemForWindow(sourceHwnd);
                _lastContentSize = _item.Size;
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
                _framePool.FrameArrived += OnFrameArrived;
                _session = _framePool.CreateCaptureSession(_item);
                TryDisableBorderAndCursor();
                _session.StartCapture();
            }
            catch { /* leave previous capture intact on failure */ }
        }
    }

    private void TryDisableBorderAndCursor()
    {
        // Best-effort. IsCursorCaptureEnabled exists in the 19041 projection; IsBorderRequired is
        // Win11-only and absent from it, so set it via reflection when the runtime supports it
        // (removes the yellow capture outline).
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        try
        {
            typeof(GraphicsCaptureSession)
                .GetProperty("IsBorderRequired")?
                .SetValue(_session, false);
        }
        catch { }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_lock)
        {
            if (_disposed) return;

            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            var texPtr = WinRtInterop.GetTexturePointer(frame.Surface);
            using var srcTexture = new ID3D11Texture2D(texPtr);
            var srcDesc = srcTexture.Description;

            EnsureMipTexture((int)srcDesc.Width, (int)srcDesc.Height);
            if (_mipTexture is null || _mipSrv is null) return;

            // Copy the freshly captured frame into mip 0, then build the chain on the GPU.
            _context.CopySubresourceRegion(_mipTexture, 0, 0, 0, 0, srcTexture, 0);
            _context.GenerateMips(_mipSrv);

            // Clear to black (letterbox bars), then draw the aspect-correct region.
            _context.OMSetRenderTargets(_backBufferRtv);
            _context.ClearRenderTargetView(_backBufferRtv, new Color4(0f, 0f, 0f, 1f));
            SetAspectViewport((int)srcDesc.Width, (int)srcDesc.Height);

            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetSampler(0, _sampler);
            _context.PSSetShaderResource(0, _mipSrv);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.Draw(3, 0);

            _swapChain.Present(1, PresentFlags.None);

            // Source resized (rare for fixed EVE clients) — rebuild the pool to match.
            var contentSize = frame.ContentSize;
            if (contentSize.Width != _lastContentSize.Width || contentSize.Height != _lastContentSize.Height)
            {
                _lastContentSize = contentSize;
                _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
            }
        }
    }

    private void SetAspectViewport(int srcW, int srcH)
    {
        var srcAspect = (double)srcW / srcH;
        var destAspect = (double)_destWidth / _destHeight;

        float drawW, drawH;
        if (destAspect > srcAspect)
        {
            drawH = _destHeight;
            drawW = (float)(_destHeight * srcAspect);
        }
        else
        {
            drawW = _destWidth;
            drawH = (float)(_destWidth / srcAspect);
        }

        _context.RSSetViewport(new Viewport(
            (_destWidth - drawW) / 2f, (_destHeight - drawH) / 2f, drawW, drawH, 0f, 1f));
    }

    private void EnsureMipTexture(int width, int height)
    {
        if (_mipTexture is not null && _mipWidth == width && _mipHeight == height) return;

        _mipSrv?.Dispose();
        _mipTexture?.Dispose();
        _mipSrv = null;
        _mipTexture = null;

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 0, // full chain
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips
        };
        _mipTexture = _device.CreateTexture2D(desc);
        _mipSrv = _device.CreateShaderResourceView(_mipTexture);
        _mipWidth = width;
        _mipHeight = height;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            // best-effort teardown; WinRT capture objects can already be torn down by the OS on disconnect
            try { if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived; } catch { }
            try { _session?.Dispose(); } catch { }
            try { _framePool?.Dispose(); } catch { }
            _mipSrv?.Dispose();
            _mipTexture?.Dispose();
            _sampler?.Dispose();
            _ps?.Dispose();
            _vs?.Dispose();
            _backBufferRtv?.Dispose();
            _swapChain?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
    }
}
