using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Baton.Media;

/// <summary>
/// Turns captured BGRA frames into the NV12 frames the H.264 encoder wants, scaled and
/// letterboxed into a fixed output size, on the GPU's video processor. The output size never
/// changes during a session, so a resized window never forces the encoder (and the phone's
/// decoder) to restart.
/// </summary>
public sealed class Nv12Converter : IDisposable
{
    private const int PoolSize = 8;

    private readonly Gpu _gpu;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11Texture2D[] _outputs = new ID3D11Texture2D[PoolSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews = new ID3D11VideoProcessorOutputView[PoolSize];
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private int _inputWidth;
    private int _inputHeight;
    private int _next;

    public Nv12Converter(Gpu gpu, int width, int height)
    {
        _gpu = gpu;
        Width = width;
        Height = height;
        _videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();
        for (var index = 0; index < PoolSize; index++)
        {
            _outputs[index] = gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget
            });
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Converts <paramref name="source"/> into the next NV12 texture of a small ring. The encoder
    /// reads each texture well before the ring comes back round to it.
    /// </summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D source, int sourceWidth, int sourceHeight) =>
        Convert(source, sourceWidth, sourceHeight, (0, 0, sourceWidth, sourceHeight));

    /// <summary>Converts only <paramref name="crop"/> (left, top, width, height) of the source.</summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D source, int sourceWidth, int sourceHeight, (int Left, int Top, int Width, int Height) crop)
    {
        crop = ClampCrop(crop, sourceWidth, sourceHeight);
        lock (_gpu.ContextLock)
        {
            EnsureProcessor(sourceWidth, sourceHeight);
            var index = _next;
            _next = (_next + 1) % PoolSize;

            using var inputView = _videoDevice.CreateVideoProcessorInputView(source, _enumerator!, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
            });

            // Fit the window into the output keeping its shape; the rest stays black.
            var scale = Math.Min((double)Width / crop.Width, (double)Height / crop.Height);
            var fitWidth = Math.Max(2, (int)(crop.Width * scale) & ~1);
            var fitHeight = Math.Max(2, (int)(crop.Height * scale) & ~1);
            var left = ((Width - fitWidth) / 2) & ~1;
            var top = ((Height - fitHeight) / 2) & ~1;
            _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true,
                new Vortice.RawRect(crop.Left, crop.Top, crop.Left + crop.Width, crop.Top + crop.Height));
            _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, new Vortice.RawRect(left, top, left + fitWidth, top + fitHeight));

            var stream = new VideoProcessorStream { Enable = true, InputSurface = inputView };
            _videoContext.VideoProcessorBlt(_processor!, _outputViews[index], 0, 1, [stream]);
            return _outputs[index];
        }
    }

    private static (int Left, int Top, int Width, int Height) ClampCrop((int Left, int Top, int Width, int Height) crop, int width, int height)
    {
        var left = Math.Clamp(crop.Left, 0, width - 1);
        var top = Math.Clamp(crop.Top, 0, height - 1);
        var right = Math.Clamp(crop.Left + crop.Width, left + 1, width);
        var bottom = Math.Clamp(crop.Top + crop.Height, top + 1, height);
        return (left, top, right - left, bottom - top);
    }

    /// <summary>The rectangle the window occupies inside the output, as fractions; for mapping touches back.</summary>
    public (double Left, double Top, double Width, double Height) ContentRect(int sourceWidth, int sourceHeight)
    {
        var scale = Math.Min((double)Width / sourceWidth, (double)Height / sourceHeight);
        var fitWidth = sourceWidth * scale / Width;
        var fitHeight = sourceHeight * scale / Height;
        return ((1 - fitWidth) / 2, (1 - fitHeight) / 2, fitWidth, fitHeight);
    }

    private void EnsureProcessor(int width, int height)
    {
        if (_processor is not null && width == _inputWidth && height == _inputHeight)
        {
            return;
        }

        DisposeProcessor();
        _inputWidth = width;
        _inputHeight = height;
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed
        });
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor { Rgba = new VideoColorRgba { A = 1 } });
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        for (var index = 0; index < PoolSize; index++)
        {
            _outputViews[index]?.Dispose();
            _outputViews[index] = _videoDevice.CreateVideoProcessorOutputView(_outputs[index], _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D
            });
        }
    }

    private void DisposeProcessor()
    {
        _processor?.Dispose();
        _enumerator?.Dispose();
        _processor = null;
        _enumerator = null;
    }

    public void Dispose()
    {
        DisposeProcessor();
        foreach (var view in _outputViews)
        {
            view?.Dispose();
        }

        foreach (var texture in _outputs)
        {
            texture.Dispose();
        }

        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
