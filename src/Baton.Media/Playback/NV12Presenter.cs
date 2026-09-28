using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Baton.Media.Playback;

public sealed record PresenterStatistics(
    long FramesPresented,
    double AveragePresentMs,
    double LastPresentMs,
    bool ZeroCopy);

/// <summary>
/// Draws decoded NV12 frames onto a window through a flip-model swap chain.
///
/// The luma and chroma planes of an NV12 texture are addressed as two shader resource views over
/// the same resource (R8 and R8G8), so the conversion to RGB happens in the pixel shader and the
/// decoded texture is never copied. When the decoder's allocator refuses SHADER_RESOURCE bind
/// flags, a GPU-side copy into an owned texture is used instead - still no readback, but one copy
/// per frame, and <see cref="PresenterStatistics.ZeroCopy"/> reports it honestly.
/// </summary>
public sealed class NV12Presenter : IDisposable
{
    private const string ShaderSource = """
        Texture2D<float>  LumaPlane   : register(t0);
        Texture2D<float2> ChromaPlane : register(t1);
        SamplerState      LinearClamp : register(s0);

        struct VertexOutput
        {
            float4 Position : SV_POSITION;
            float2 Texture  : TEXCOORD0;
        };

        // Fullscreen triangle from the vertex id alone: no vertex buffer, no input layout.
        VertexOutput VSMain(uint vertexId : SV_VertexID)
        {
            VertexOutput output;
            output.Texture = float2((vertexId << 1) & 2, vertexId & 2);
            output.Position = float4(output.Texture * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            return output;
        }

        // BT.709, limited range (16-235 luma, 16-240 chroma) to full-range RGB. This is what
        // Android's MediaCodec emits unless a stream explicitly says otherwise.
        float4 PSMain(VertexOutput input) : SV_TARGET
        {
            float  y  = LumaPlane.Sample(LinearClamp, input.Texture);
            float2 uv = ChromaPlane.Sample(LinearClamp, input.Texture);

            y  = (y - 0.0625) * 1.164383;
            uv -= float2(0.5, 0.5);

            float r = y + 1.792741 * uv.y;
            float g = y - 0.213249 * uv.x - 0.532909 * uv.y;
            float b = y + 2.112402 * uv.x;

            return float4(saturate(float3(r, g, b)), 1.0);
        }
        """;

    private readonly D3D11Context _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly bool _allowTearing;

    private ID3D11RenderTargetView _renderTarget;
    private ID3D11Texture2D? _copyTarget;
    private int _width;
    private int _height;

    private long _framesPresented;
    private double _totalPresentMs;
    private double _lastPresentMs;
    private bool _zeroCopy = true;
    private int _videoWidth;
    private int _videoHeight;

    private NV12Presenter(
        D3D11Context context,
        IDXGISwapChain1 swapChain,
        ID3D11VertexShader vertexShader,
        ID3D11PixelShader pixelShader,
        ID3D11SamplerState sampler,
        ID3D11RenderTargetView renderTarget,
        bool allowTearing,
        int width,
        int height)
    {
        _context = context;
        _swapChain = swapChain;
        _vertexShader = vertexShader;
        _pixelShader = pixelShader;
        _sampler = sampler;
        _renderTarget = renderTarget;
        _allowTearing = allowTearing;
        _width = width;
        _height = height;
    }

    public PresenterStatistics Statistics => new(
        _framesPresented,
        _framesPresented == 0 ? 0 : _totalPresentMs / _framesPresented,
        _lastPresentMs,
        _zeroCopy);

    public static NV12Presenter Create(D3D11Context context, nint windowHandle, int width, int height)
    {
        using var dxgiDevice = context.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var allowTearing = false;
        using (var factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
        {
            allowTearing = factory5?.PresentAllowTearing ?? false;
        }

        var description = new SwapChainDescription1
        {
            Width = (uint)Math.Max(width, 1),
            Height = (uint)Math.Max(height, 1),
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            // Flip-discard is the only presentation model without an extra compositor copy, and
            // tearing is what removes the last frame of latency on a variable-rate display.
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None
        };

        var swapChain = factory.CreateSwapChainForHwnd(context.Device, windowHandle, description);
        factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);

        var vertexBlob = Compiler.Compile(ShaderSource, "VSMain", "nv12.hlsl", "vs_5_0");
        var pixelBlob = Compiler.Compile(ShaderSource, "PSMain", "nv12.hlsl", "ps_5_0");
        var vertexShader = context.Device.CreateVertexShader(vertexBlob.Span);
        var pixelShader = context.Device.CreatePixelShader(pixelBlob.Span);

        var sampler = context.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue
        });

        using var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        var renderTarget = context.Device.CreateRenderTargetView(backBuffer);

        return new NV12Presenter(
            context,
            swapChain,
            vertexShader,
            pixelShader,
            sampler,
            renderTarget,
            allowTearing,
            (int)description.Width,
            (int)description.Height);
    }

    /// <summary>Size of the video being presented, used to letterbox it into the surface.</summary>
    public void SetVideoSize(int width, int height)
    {
        _videoWidth = width;
        _videoHeight = height;
    }

    /// <summary>
    /// The largest rectangle of the video's aspect that fits the surface, centred. Shared by
    /// rendering and input mapping so the two can never disagree.
    /// </summary>
    public static (int X, int Y, int Width, int Height) FitRect(int surfaceWidth, int surfaceHeight, int videoWidth, int videoHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || videoWidth <= 0 || videoHeight <= 0)
        {
            return (0, 0, Math.Max(surfaceWidth, 1), Math.Max(surfaceHeight, 1));
        }

        var scale = Math.Min((double)surfaceWidth / videoWidth, (double)surfaceHeight / videoHeight);
        var width = Math.Max(1, (int)Math.Round(videoWidth * scale));
        var height = Math.Max(1, (int)Math.Round(videoHeight * scale));
        return ((surfaceWidth - width) / 2, (surfaceHeight - height) / 2, width, height);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        if (width == _width && height == _height)
        {
            return;
        }

        _context.ImmediateContext.UnsetRenderTargets();
        _renderTarget.Dispose();

        _swapChain.ResizeBuffers(
            2,
            (uint)width,
            (uint)height,
            Format.B8G8R8A8_UNorm,
            _allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None);

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTarget = _context.Device.CreateRenderTargetView(backBuffer);
        _width = width;
        _height = height;
    }

    /// <summary>Draws one frame and presents it. The frame may be disposed as soon as this returns.</summary>
    public void Present(DecodedFrame frame)
    {
        var started = Stopwatch.GetTimestamp();
        var deviceContext = _context.ImmediateContext;

        var source = ResolveSampleableTexture(frame, out var subresource);
        var arraySlice = source.Description.ArraySize > 1 ? subresource : 0;

        using var lumaView = _context.Device.CreateShaderResourceView(source, new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2DArray,
            Texture2DArray = new Texture2DArrayShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1,
                FirstArraySlice = (uint)arraySlice,
                ArraySize = 1
            }
        });

        using var chromaView = _context.Device.CreateShaderResourceView(source, new ShaderResourceViewDescription
        {
            Format = Format.R8G8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2DArray,
            Texture2DArray = new Texture2DArrayShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1,
                FirstArraySlice = (uint)arraySlice,
                ArraySize = 1
            }
        });

        deviceContext.OMSetRenderTargets(_renderTarget);

        // Letterbox rather than stretch: the phone's aspect is preserved at any window size, and
        // the same rectangle is what input mapping uses, so a click lands where it looks like it.
        deviceContext.ClearRenderTargetView(_renderTarget, new Color4(0.02f, 0.027f, 0.04f, 1f));
        var fit = FitRect(_width, _height, _videoWidth > 0 ? _videoWidth : _width, _videoHeight > 0 ? _videoHeight : _height);
        deviceContext.RSSetViewport(new Viewport(fit.X, fit.Y, fit.Width, fit.Height));
        deviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        deviceContext.VSSetShader(_vertexShader);
        deviceContext.PSSetShader(_pixelShader);
        deviceContext.PSSetShaderResources(0, new[] { lumaView, chromaView });
        deviceContext.PSSetSampler(0, _sampler);
        deviceContext.Draw(3, 0);

        _swapChain.Present(0, _allowTearing ? PresentFlags.AllowTearing : PresentFlags.None);

        // Leave nothing bound: the decoder writes into this texture again on the next frame.
        deviceContext.PSSetShaderResources(0, new ID3D11ShaderResourceView?[] { null, null });

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _lastPresentMs = elapsedMs;
        _totalPresentMs += elapsedMs;
        _framesPresented++;
    }

    private ID3D11Texture2D ResolveSampleableTexture(DecodedFrame frame, out int subresource)
    {
        var description = frame.Texture.Description;
        if ((description.BindFlags & BindFlags.ShaderResource) != 0)
        {
            subresource = frame.Subresource;
            return frame.Texture;
        }

        _zeroCopy = false;
        EnsureCopyTarget(description);
        _context.ImmediateContext.CopySubresourceRegion(
            _copyTarget!,
            0,
            0,
            0,
            0,
            frame.Texture,
            (uint)frame.Subresource);

        subresource = 0;
        return _copyTarget!;
    }

    private void EnsureCopyTarget(Texture2DDescription source)
    {
        if (_copyTarget is not null
            && _copyTarget.Description.Width == source.Width
            && _copyTarget.Description.Height == source.Height)
        {
            return;
        }

        _copyTarget?.Dispose();
        _copyTarget = _context.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });
    }

    /// <summary>
    /// Copies the last presented back buffer into system memory as BGRA rows. Used only to prove
    /// the colour conversion is right; the normal path never reads back from the GPU.
    /// </summary>
    public (byte[] Pixels, int Width, int Height, int Stride) CaptureBackBuffer()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        var description = backBuffer.Description;

        using var staging = _context.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = description.Width,
            Height = description.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = description.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });

        _context.ImmediateContext.CopyResource(staging, backBuffer);

        var mapped = _context.ImmediateContext.Map(staging, 0, MapMode.Read);
        try
        {
            var stride = (int)mapped.RowPitch;
            var pixels = new byte[stride * (int)description.Height];
            System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer, pixels, 0, pixels.Length);
            return (pixels, (int)description.Width, (int)description.Height, stride);
        }
        finally
        {
            _context.ImmediateContext.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        _copyTarget?.Dispose();
        _renderTarget.Dispose();
        _sampler.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _swapChain.Dispose();
    }
}
