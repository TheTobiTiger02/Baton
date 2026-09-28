using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Baton.Media.Playback;

/// <summary>A decoded frame still owned by the decoder's sample; dispose to return it.</summary>
public sealed class DecodedFrame : IDisposable
{
    private readonly IMFSample _sample;
    private readonly IMFMediaBuffer _buffer;

    internal DecodedFrame(IMFSample sample, IMFMediaBuffer buffer, ID3D11Texture2D texture, int subresource, long presentationTimeUs)
    {
        _sample = sample;
        _buffer = buffer;
        Texture = texture;
        Subresource = subresource;
        PresentationTimeUs = presentationTimeUs;
    }

    /// <summary>NV12 texture owned by the decoder's output pool. Never copied.</summary>
    public ID3D11Texture2D Texture { get; }

    /// <summary>Index into the texture array; hardware decoders hand out array slices, not single textures.</summary>
    public int Subresource { get; }

    public long PresentationTimeUs { get; }

    public void Dispose()
    {
        Texture.Dispose();
        _buffer.Dispose();
        _sample.Dispose();
    }
}

public sealed record DecoderStatistics(
    long FramesDecoded,
    long FramesDropped,
    double AverageDecodeMs,
    double LastDecodeMs,
    double PeakDecodeMs,
    long SystemMemoryFrames = 0);

/// <summary>
/// Hardware H.264/H.265 decoder built on a Media Foundation Transform bound to a D3D11 device.
///
/// Output samples carry <c>IMFDXGIBuffer</c>, so a decoded frame is an NV12 texture the presenter
/// samples directly - there is no readback and no format conversion on the CPU. Modern drivers
/// expose the decoder as an asynchronous MFT, which has a different calling contract from the
/// software one, so both are supported and selected from the MFT's own attributes.
/// </summary>
public sealed class VideoDecoder : IDisposable
{
    // MFT_ENUM_FLAG_*
    private const uint EnumSyncMft = 0x00000001;
    private const uint EnumAsyncMft = 0x00000002;
    private const uint EnumHardware = 0x00000004;
    private const uint EnumSortAndFilter = 0x00000040;

    private static readonly Guid MfLowLatency = new("9C27891A-ED7A-40e1-88E8-B22727A024EE");
    private static readonly Guid MfMtMpeg2Sequence = new("3C36A331-3195-4051-BC1F-AC71AC79EE2B");
    private static readonly Guid MajorTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // MFT_OUTPUT_STREAM_INFO_FLAGS
    private const int OutputProvidesSamples = 0x00000100;
    private const int OutputCanProvideSamples = 0x00000200;

    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);

    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly bool _isAsync;
    private readonly int _inputStreamId;
    private readonly int _outputStreamId;

    private long _framesDecoded;
    private long _framesDropped;
    private long _systemMemoryFrames;
    private double _totalDecodeMs;
    private double _lastDecodeMs;
    private double _peakDecodeMs;
    private long _pendingInputAllowance;
    private bool _providesSamples;
    private int _outputSampleSize;

    private VideoDecoder(IMFTransform transform, bool isAsync, string name, bool isHardware, int inputStreamId, int outputStreamId)
    {
        _transform = transform;
        _isAsync = isAsync;
        Name = name;
        IsHardware = isHardware;
        _inputStreamId = inputStreamId;
        _outputStreamId = outputStreamId;

        if (isAsync)
        {
            _events = transform.QueryInterface<IMFMediaEventGenerator>();
        }
    }

    public string Name { get; }

    public bool IsHardware { get; }

    public bool IsAsync => _isAsync;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>
    /// True when the transform hands out its own (D3D11-backed) output samples. False means the
    /// caller allocates system memory, which also means there is no zero-copy path.
    /// </summary>
    public bool ProvidesSamples => _providesSamples;

    public event Action<DecodedFrame>? FrameDecoded;

    public DecoderStatistics Statistics => new(
        _framesDecoded,
        _framesDropped,
        _framesDecoded == 0 ? 0 : _totalDecodeMs / _framesDecoded,
        _lastDecodeMs,
        _peakDecodeMs,
        _systemMemoryFrames);

    /// <summary>
    /// Creates a decoder for <paramref name="codecSubtype"/> (<see cref="VideoFormatGuids.H264"/>
    /// or <see cref="VideoFormatGuids.H265"/>) bound to <paramref name="context"/>'s device.
    /// </summary>
    public static VideoDecoder Create(
        D3D11Context context,
        Guid codecSubtype,
        int width,
        int height,
        ReadOnlySpan<byte> parameterSets,
        bool preferHardware = true)
    {
        var (transform, name, isHardware) = SelectTransform(codecSubtype, preferHardware);

        var attributes = transform.Attributes;
        var isAsync = SafeGetUInt32(attributes, TransformAttributeKeys.TransformAsync) != 0;
        if (isAsync)
        {
            // Required handshake: until the MFT is unlocked it refuses every other call.
            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        }

        // Best-effort: not every decoder exposes it, and a decoder that ignores it still works.
        TrySet(attributes, MfLowLatency, 1u);

        // Most decoders report E_NOTIMPL here, which means "stream 0 for both" by contract.
        var inputIds = new int[1];
        var outputIds = new int[1];
        var inputStreamId = 0;
        var outputStreamId = 0;
        try
        {
            transform.GetStreamIDs(1, inputIds, 1, outputIds);
            inputStreamId = inputIds[0];
            outputStreamId = outputIds[0];
        }
        catch (SharpGenException)
        {
            // Fixed stream identifiers; the defaults above are correct.
        }

        var decoder = new VideoDecoder(transform, isAsync, name, isHardware, inputStreamId, outputStreamId)
        {
            Width = width,
            Height = height
        };

        decoder.BindDeviceManager(context);
        decoder.ConfigureTypes(codecSubtype, width, height, parameterSets);
        decoder.ReadOutputStreamInfo();
        decoder.BeginStreaming();
        return decoder;
    }

    private void BindDeviceManager(D3D11Context context)
    {
        // Handing the MFT the device manager is what moves decode output into D3D11 textures.
        var manager = Marshal.GetIUnknownForObject(context.DeviceManager);
        try
        {
            _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)(nint)context.DeviceManager.NativePointer);
        }
        finally
        {
            if (manager != IntPtr.Zero)
            {
                Marshal.Release(manager);
            }
        }
    }

    private void ConfigureTypes(Guid codecSubtype, int width, int height, ReadOnlySpan<byte> parameterSets)
    {
        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MajorTypeVideo);
        inputType.Set(MediaTypeAttributeKeys.Subtype, codecSubtype);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, PackSize(width, height));
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // MFVideoInterlace_Progressive

        if (!parameterSets.IsEmpty)
        {
            // Giving the decoder SPS/PPS up front lets it produce output from the very first
            // access unit instead of waiting for an in-band parameter set.
            inputType.SetBlob(MfMtMpeg2Sequence, parameterSets.ToArray());
        }

        _transform.SetInputType(_inputStreamId, inputType, 0);

        // DXVA output textures are created with the decoder bind flag only. Asking the MFT's
        // allocator for SHADER_RESOURCE as well is what lets the presenter sample the decoded
        // texture in place; without it every frame needs a GPU-side copy first.
        try
        {
            var outputAttributes = _transform.GetOutputStreamAttributes(_outputStreamId);
            outputAttributes.Set(
                TransformAttributeKeys.D3D11Bindflags,
                (uint)(BindFlags.ShaderResource | BindFlags.Decoder));
        }
        catch (SharpGenException)
        {
            // Allocator keeps its defaults; NV12Presenter falls back to a copy.
        }

        // Walk the decoder's own output list rather than inventing a type: the NV12 entry it
        // offers already carries the stride and alignment its allocator will use.
        IMFMediaType? chosen = null;
        for (var index = 0; ; index++)
        {
            IMFMediaType candidate;
            try
            {
                candidate = _transform.GetOutputAvailableType(_outputStreamId, index);
            }
            catch (SharpGenException)
            {
                break;
            }

            if (candidate.GetGUID(MediaTypeAttributeKeys.Subtype) == VideoFormatGuids.NV12)
            {
                chosen = candidate;
                break;
            }

            candidate.Dispose();
        }

        if (chosen is null)
        {
            throw new InvalidOperationException($"{Name} does not offer an NV12 output type.");
        }

        using (chosen)
        {
            _transform.SetOutputType(_outputStreamId, chosen, 0);
        }
    }

    private void ReadOutputStreamInfo()
    {
        var info = _transform.GetOutputStreamInfo(_outputStreamId);
        _providesSamples = (info.Flags & (OutputProvidesSamples | OutputCanProvideSamples)) != 0;
        _outputSampleSize = info.Size;
    }

    private void BeginStreaming()
    {
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    /// <summary>
    /// Submits one access unit. Decoded frames are raised on <see cref="FrameDecoded"/>, on this
    /// thread for a synchronous MFT and from <see cref="PumpAsync"/> for an asynchronous one.
    /// </summary>
    public void Decode(ReadOnlySpan<byte> accessUnit, long presentationTimeUs, bool isKeyframe)
    {
        using var sample = BuildSample(accessUnit, presentationTimeUs, isKeyframe);

        if (_isAsync)
        {
            // The async contract is strict: input is only legal after METransformNeedInput.
            WaitForInputAllowance();
            _transform.ProcessInput(_inputStreamId, sample, 0);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        _transform.ProcessInput(_inputStreamId, sample, 0);
        DrainSynchronousOutput(started);
    }

    private IMFSample BuildSample(ReadOnlySpan<byte> accessUnit, long presentationTimeUs, bool isKeyframe)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(accessUnit.Length);
        buffer.Lock(out var destination, out _, out _);
        try
        {
            unsafe
            {
                accessUnit.CopyTo(new Span<byte>((void*)destination, accessUnit.Length));
            }
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = accessUnit.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = presentationTimeUs * 10; // 100 ns units
        if (isKeyframe)
        {
            sample.SampleFlags = 1; // MFSampleExtension_CleanPoint
        }

        buffer.Dispose();
        return sample;
    }

    private void DrainSynchronousOutput(long startedTimestamp)
    {
        while (TryProcessOutput(startedTimestamp))
        {
        }
    }

    /// <summary>
    /// Pulls one decoded frame. Returns false when the transform needs more input, which is the
    /// normal way a drain loop ends; any other failure is a real error and is thrown.
    /// </summary>
    private bool TryProcessOutput(long startedTimestamp)
    {
        var buffers = new OutputDataBuffer[1];
        buffers[0].StreamID = _outputStreamId;

        // A transform that does not allocate its own samples expects the caller to supply one.
        // That path is system memory only, so it is also the path that loses zero copy.
        IMFSample? allocated = null;
        if (!_providesSamples)
        {
            allocated = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(_outputSampleSize, 1));
            allocated.AddBuffer(buffer);
            buffers[0].Sample = allocated;
        }

        var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffers[0], out _);

        if (result.Failure)
        {
            allocated?.Dispose();

            if (result.Code == MfETransformNeedMoreInput)
            {
                return false;
            }

            if (result.Code == MfETransformStreamChange)
            {
                // The decoder learned the real frame size from the stream; re-negotiating the
                // output type is the documented response.
                RenegotiateOutputType();
                return true;
            }

            result.CheckError();
            return false;
        }

        RaiseFrame(buffers[0].Sample, startedTimestamp);
        return true;
    }

    private void RenegotiateOutputType()
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType candidate;
            try
            {
                candidate = _transform.GetOutputAvailableType(_outputStreamId, index);
            }
            catch (SharpGenException)
            {
                return;
            }

            using (candidate)
            {
                if (candidate.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                {
                    continue;
                }

                _transform.SetOutputType(_outputStreamId, candidate, 0);
                var size = candidate.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                Width = (int)(size >> 32);
                Height = (int)(size & 0xFFFFFFFF);
                ReadOutputStreamInfo();
                return;
            }
        }
    }

    /// <summary>
    /// Runs the asynchronous MFT's event loop until cancelled. Only meaningful when
    /// <see cref="IsAsync"/>; a synchronous decoder returns immediately.
    /// </summary>
    public void PumpAsync(CancellationToken cancellationToken)
    {
        if (_events is null)
        {
            return;
        }

        var inputStarted = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
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
                    case 601: // METransformNeedInput
                        Interlocked.Increment(ref _pendingInputAllowance);
                        inputStarted = Stopwatch.GetTimestamp();
                        break;

                    case 602: // METransformHaveOutput
                        DrainAsyncOutput(inputStarted);
                        break;

                    case 603: // METransformDrainComplete
                        return;
                }
            }
        }
    }

    private void DrainAsyncOutput(long startedTimestamp)
    {
        try
        {
            TryProcessOutput(startedTimestamp);
        }
        catch (SharpGenException)
        {
            Interlocked.Increment(ref _framesDropped);
        }
    }

    private void RaiseFrame(IMFSample? sample, long startedTimestamp)
    {
        if (sample is null)
        {
            return;
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        _lastDecodeMs = elapsedMs;
        _totalDecodeMs += elapsedMs;
        _peakDecodeMs = Math.Max(_peakDecodeMs, elapsedMs);
        Interlocked.Increment(ref _framesDecoded);

        var buffer = sample.GetBufferByIndex(0);
        using var dxgiBuffer = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
        if (dxgiBuffer is null)
        {
            // System-memory output. It decodes correctly but cannot be presented without a CPU
            // upload, so it is counted separately rather than passed off as a rendered frame.
            buffer.Dispose();
            sample.Dispose();
            Interlocked.Increment(ref _systemMemoryFrames);
            return;
        }

        var resourcePointer = dxgiBuffer.GetResource(Texture2DIid);
        var texture = new ID3D11Texture2D(resourcePointer);
        var subresource = (int)dxgiBuffer.SubresourceIndex;

        FrameDecoded?.Invoke(new DecodedFrame(sample, buffer, texture, subresource, sample.SampleTime / 10));
    }

    private void WaitForInputAllowance()
    {
        var spin = new SpinWait();
        while (Interlocked.Read(ref _pendingInputAllowance) <= 0)
        {
            spin.SpinOnce();
        }

        Interlocked.Decrement(ref _pendingInputAllowance);
    }

    /// <summary>
    /// Every decoder Windows registers for this codec, in the order MFTEnumEx returns them.
    /// Reported by the spike harness so decoder selection is visible rather than assumed.
    /// </summary>
    public static IReadOnlyList<(string Name, bool IsHardware)> EnumerateDecoders(Guid codecSubtype)
    {
        var found = new List<(string, bool)>();
        EnumerateActivates(codecSubtype, preferHardware: true, (activate, _) =>
        {
            found.Add((
                SafeGetString(activate, TransformAttributeKeys.MftFriendlyNameAttribute) ?? "unnamed decoder",
                SafeGetString(activate, TransformAttributeKeys.MftEnumHardwareUrlAttribute) is not null));
            return false;
        });

        return found;
    }

    private static (IMFTransform Transform, string Name, bool IsHardware) SelectTransform(Guid codecSubtype, bool preferHardware)
    {
        (IMFTransform Transform, string Name, bool IsHardware)? selected = null;
        (IMFTransform Transform, string Name, bool IsHardware)? firstUsable = null;

        EnumerateActivates(codecSubtype, preferHardware, (activate, _) =>
        {
            var name = SafeGetString(activate, TransformAttributeKeys.MftFriendlyNameAttribute) ?? "unnamed decoder";
            var isHardware = SafeGetString(activate, TransformAttributeKeys.MftEnumHardwareUrlAttribute) is not null;

            IMFTransform transform;
            try
            {
                transform = activate.ActivateObject<IMFTransform>();
            }
            catch (SharpGenException)
            {
                // A registered decoder that will not activate (missing driver component) is not
                // an error; the next candidate is tried instead.
                return false;
            }

            if (!preferHardware || isHardware)
            {
                selected = (transform, name, isHardware);
                return true;
            }

            if (firstUsable is null)
            {
                firstUsable = (transform, name, isHardware);
            }
            else
            {
                transform.Dispose();
            }

            return false;
        });

        var chosen = selected ?? firstUsable;
        if (chosen is null)
        {
            throw new InvalidOperationException($"No usable Media Foundation decoder for {codecSubtype}.");
        }

        if (selected is not null && firstUsable is not null)
        {
            firstUsable.Value.Transform.Dispose();
        }

        return chosen.Value;
    }

    /// <summary>
    /// Walks the registered decoders for a codec. <paramref name="visit"/> returns true to stop.
    /// The activate array comes from CoTaskMem and must be freed exactly once.
    /// </summary>
    private static void EnumerateActivates(Guid codecSubtype, bool preferHardware, Func<IMFActivate, int, bool> visit)
    {
        var inputInfo = new RegisterTypeInfo
        {
            GuidMajorType = MajorTypeVideo,
            GuidSubtype = codecSubtype
        };

        var flags = EnumSortAndFilter | EnumSyncMft | EnumAsyncMft;
        if (preferHardware)
        {
            flags |= EnumHardware;
        }

        MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            flags,
            inputInfo,
            null,
            out var activatePointers,
            out var count);

        if (count == 0 || activatePointers == IntPtr.Zero)
        {
            throw new InvalidOperationException($"No Media Foundation decoder is registered for {codecSubtype}.");
        }

        try
        {
            for (var index = 0; index < count; index++)
            {
                var pointer = Marshal.ReadIntPtr(activatePointers, index * IntPtr.Size);
                using var activate = new IMFActivate(pointer);
                if (visit(activate, index))
                {
                    return;
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(activatePointers);
        }
    }

    /// <summary>Absent attributes are the norm on MFTs; MF_E_ATTRIBUTENOTFOUND means "no".</summary>
    private static uint SafeGetUInt32(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetUInt32(key);
        }
        catch (SharpGenException)
        {
            return 0;
        }
    }

    private static string? SafeGetString(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetString(key);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private static void TrySet(IMFAttributes attributes, Guid key, uint value)
    {
        try
        {
            attributes.Set(key, value);
        }
        catch (SharpGenException)
        {
            // Optional hint; a decoder that rejects it still decodes correctly.
        }
    }

    private static ulong PackSize(int width, int height) => ((ulong)(uint)width << 32) | (uint)height;

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
        }
        catch (SharpGenException)
        {
            // The transform may already be torn down by a device loss.
        }

        _events?.Dispose();
        _transform.Dispose();
    }
}
