using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Baton.Media;

/// <summary>An encoded access unit in Annex-B form, ready for the wire.</summary>
public readonly record struct EncodedFrame(byte[] Data, long PresentationTimeUs, bool IsKeyframe);

/// <summary>
/// Hardware H.264 encoder (NVENC, Quick Sync or AMF, whichever the GPU offers) driven through
/// Media Foundation, configured for streaming: low latency, constant bitrate, no B-frames, a
/// keyframe every two seconds so a viewer that joins or drops a frame recovers quickly.
///
/// Hardware encoder MFTs are asynchronous: input is legal only after the transform asks for it,
/// and output is announced by events. A dedicated thread runs that event loop; frames offered when
/// the encoder is not ready are dropped rather than queued, which is what keeps latency flat.
/// </summary>
public sealed class H264Encoder : IDisposable
{
    private const uint EnumAsyncMft = 0x2, EnumHardware = 0x4, EnumSortAndFilter = 0x40;
    private const int MeTransformNeedInput = 601, MeTransformHaveOutput = 602;
    private const int OutputProvidesSamples = 0x100, OutputCanProvideSamples = 0x200;

    private static readonly Guid MajorTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid MtMpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MtMaxKeyframeSpacing = new("c16eb52b-73a1-476f-8d62-839d6a020652");
    private static readonly Guid Texture2DIid = typeof(ID3D11Texture2D).GUID;

    // ICodecAPI properties.
    private static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly ICodecAPI? _codecApi;
    private readonly Thread _pump;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _inputGate = new();
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private int _inputAllowance;

    private H264Encoder(IMFTransform transform, string name, int width, int height, int fps, int bitrate)
    {
        _transform = transform;
        Name = name;
        Width = width;
        Height = height;
        _events = transform.QueryInterface<IMFMediaEventGenerator>();
        _codecApi = CodecApi.From(transform);

        var info = transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (OutputProvidesSamples | OutputCanProvideSamples)) != 0;
        _outputSize = Math.Max(info.Size, width * height);
        _pump = new Thread(Pump) { IsBackground = true, Name = "Baton H.264 encoder" };
        _ = fps;
        _ = bitrate;
    }

    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Raised on the encoder thread for each encoded access unit.</summary>
    public event Action<EncodedFrame>? FrameEncoded;

    public static H264Encoder Create(Gpu gpu, int width, int height, int fps = 60, int bitrate = 12_000_000)
    {
        var (transform, name) = FindHardwareEncoder();
        var attributes = transform.Attributes;
        attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        TrySet(attributes, LowLatencyMode, 1u);
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)(nint)gpu.DeviceManager.NativePointer);

        var codec = CodecApi.From(transform);
        codec?.TrySet(LowLatencyMode, true);
        codec?.TrySet(RateControlMode, 0u); // eAVEncCommonRateControlMode_CBR
        codec?.TrySet(MeanBitRate, (uint)bitrate);
        codec?.TrySet(GopSize, (uint)(fps * 2));
        codec?.TrySet(BPictureCount, 0u);

        // Encoders want the output type first; it decides which input types they accept.
        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MajorTypeVideo);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            output.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            output.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
            output.Set(MtMpeg2Profile, 100u); // High
            output.Set(MtMaxKeyframeSpacing, (uint)(fps * 2));
            transform.SetOutputType(0, output, 0);
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MajorTypeVideo);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            transform.SetInputType(0, input, 0);
        }

        var encoder = new H264Encoder(transform, name, width, height, fps, bitrate);
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        encoder._pump.Start();
        return encoder;
    }

    /// <summary>
    /// Offers one NV12 frame. Returns false (and drops it) when the encoder is still busy with the
    /// previous ones.
    /// </summary>
    public bool TryEncode(ID3D11Texture2D nv12, long presentationTimeUs)
    {
        lock (_inputGate)
        {
            if (_inputAllowance <= 0)
            {
                return false;
            }

            _inputAllowance--;
        }

        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(Texture2DIid, nv12, 0, false);
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = presentationTimeUs * 10;
        sample.SampleDuration = 166_667;
        try
        {
            _transform.ProcessInput(0, sample, 0);
            return true;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    /// <summary>Makes the next frame a keyframe, e.g. after the viewer reconnects.</summary>
    public void RequestKeyframe() => _codecApi?.TrySet(ForceKeyFrame, 1u);

    /// <summary>Changes the target bitrate of the running stream (CBR), for a link that slowed down or recovered.</summary>
    public void SetBitrate(int bitsPerSecond) => _codecApi?.TrySet(MeanBitRate, (uint)bitsPerSecond);

    private void Pump()
    {
        while (!_stop.IsCancellationRequested)
        {
            IMFMediaEvent mediaEvent;
            try
            {
                mediaEvent = _events.GetEvent(0);
            }
            catch (SharpGenException)
            {
                return;
            }

            using (mediaEvent)
            {
                switch ((int)mediaEvent.EventType)
                {
                    case MeTransformNeedInput:
                        lock (_inputGate)
                        {
                            _inputAllowance++;
                        }

                        break;
                    case MeTransformHaveOutput:
                        DrainOutput();
                        break;
                }
            }
        }
    }

    private void DrainOutput()
    {
        var buffer = new OutputDataBuffer { StreamID = 0 };
        IMFSample? allocated = null;
        if (!_providesSamples)
        {
            allocated = MediaFactory.MFCreateSample();
            using var memory = MediaFactory.MFCreateMemoryBuffer(_outputSize);
            allocated.AddBuffer(memory);
            buffer.Sample = allocated;
        }

        var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
        buffer.Events?.Dispose();
        if (result.Failure || buffer.Sample is null)
        {
            allocated?.Dispose();
            return;
        }

        using var sample = buffer.Sample;
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var pointer, out _, out var length);
        byte[] data;
        try
        {
            data = new byte[length];
            Marshal.Copy(pointer, data, 0, length);
        }
        finally
        {
            contiguous.Unlock();
        }

        var keyframe = false;
        try
        {
            keyframe = sample.GetUInt32(CleanPoint) != 0;
        }
        catch (SharpGenException)
        {
        }

        FrameEncoded?.Invoke(new EncodedFrame(data, sample.SampleTime / 10, keyframe));
    }

    private static (IMFTransform Transform, string Name) FindHardwareEncoder()
    {
        var output = new RegisterTypeInfo { GuidMajorType = MajorTypeVideo, GuidSubtype = VideoFormatGuids.H264 };
        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, EnumHardware | EnumAsyncMft | EnumSortAndFilter,
            null, output, out var activates, out var count);
        if (count == 0 || activates == IntPtr.Zero)
        {
            throw new InvalidOperationException("This PC has no hardware H.264 encoder.");
        }

        try
        {
            for (var index = 0; index < count; index++)
            {
                using var activate = new IMFActivate(Marshal.ReadIntPtr(activates, index * IntPtr.Size));
                try
                {
                    var name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                    return (activate.ActivateObject<IMFTransform>(), name);
                }
                catch (SharpGenException)
                {
                    // Registered but not usable (driver component missing); try the next one.
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(activates);
        }

        throw new InvalidOperationException("No hardware H.264 encoder could be started.");
    }

    private static void TrySet(IMFAttributes attributes, Guid key, uint value)
    {
        try
        {
            attributes.Set(key, value);
        }
        catch (SharpGenException)
        {
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
            _transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
        }
        catch (SharpGenException)
        {
        }

        // GetEvent blocks until the transform posts something; shutting the generator down
        // releases the pump thread.
        try
        {
            using var shutdown = _transform.QueryInterfaceOrNull<IMFShutdown>();
            shutdown?.Shutdown();
        }
        catch (SharpGenException)
        {
        }

        _pump.Join(TimeSpan.FromSeconds(2));
        _events.Dispose();
        _transform.Dispose();
    }
}

/// <summary>The encoder's ICodecAPI, for the settings that media types cannot express.</summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported([In] ref Guid api);
    [PreserveSig] int IsModifiable([In] ref Guid api);
    [PreserveSig] int GetParameterRange([In] ref Guid api, IntPtr min, IntPtr max, IntPtr step);
    [PreserveSig] int GetParameterValues([In] ref Guid api, IntPtr values, IntPtr count);
    [PreserveSig] int GetDefaultValue([In] ref Guid api, IntPtr value);
    [PreserveSig] int GetValue([In] ref Guid api, IntPtr value);
    [PreserveSig] int SetValue([In] ref Guid api, [In] ref PropVariant value);
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort VarType;
    [FieldOffset(8)] public uint UInt32;
    [FieldOffset(8)] public short Bool;

    public static PropVariant From(uint value) => new() { VarType = 19, UInt32 = value }; // VT_UI4

    public static PropVariant From(bool value) => new() { VarType = 11, Bool = (short)(value ? -1 : 0) }; // VT_BOOL
}

internal static class CodecApi
{
    public static ICodecAPI? From(IMFTransform transform)
    {
        var iid = typeof(ICodecAPI).GUID;
        if (Marshal.QueryInterface(transform.NativePointer, ref iid, out var pointer) != 0)
        {
            return null;
        }

        try
        {
            return (ICodecAPI)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    public static void TrySet(this ICodecAPI api, Guid key, uint value)
    {
        var variant = PropVariant.From(value);
        api.SetValue(ref key, ref variant);
    }

    public static void TrySet(this ICodecAPI api, Guid key, bool value)
    {
        var variant = PropVariant.From(value);
        api.SetValue(ref key, ref variant);
    }
}
