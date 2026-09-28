using System.Runtime.InteropServices;

namespace Baton.Media;

/// <summary>
/// Captures the sound of one app (and its child processes) with Windows' per-process loopback,
/// as 48 kHz 16-bit stereo PCM. Other apps' sounds and notifications are not included.
/// </summary>
public sealed class AudioLoopback : IDisposable
{
    public const int SampleRate = 48_000, Channels = 2, BitsPerSample = 16;

    private const uint StreamFlagsLoopback = 0x00020000, StreamFlagsEventCallback = 0x00040000, StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint BufferFlagsSilent = 0x2;
    private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientIid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly int _processId;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    private AudioLoopback(int processId) => _processId = processId;

    /// <summary>Raised on the capture thread with a PCM buffer and the number of valid bytes in it.</summary>
    public event Action<byte[], int>? Captured;

    public static AudioLoopback ForWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return new AudioLoopback((int)processId);
    }

    public void Start()
    {
        var client = Activate(_processId);
        _thread = new Thread(() => Run(client)) { IsBackground = true, Name = "Baton audio loopback" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    private void Run(IAudioClient client)
    {
        using var ready = new AutoResetEvent(false);
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = Channels,
            SamplesPerSec = SampleRate,
            AvgBytesPerSec = SampleRate * Channels * BitsPerSample / 8,
            BlockAlign = Channels * BitsPerSample / 8,
            BitsPerSample = BitsPerSample
        };
        Check(client.Initialize(0, StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm, 200_000, 0, ref format, IntPtr.Zero));
        Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
        var iid = AudioCaptureClientIid;
        Check(client.GetService(ref iid, out var service));
        var capture = (IAudioCaptureClient)service;
        Check(client.Start());

        var buffer = new byte[SampleRate * format.BlockAlign / 5];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (!ready.WaitOne(200))
                {
                    continue;
                }

                while (capture.GetNextPacketSize(out var frames) == 0 && frames > 0)
                {
                    if (capture.GetBuffer(out var data, out var count, out var flags, out _, out _) != 0)
                    {
                        break;
                    }

                    var bytes = (int)count * format.BlockAlign;
                    if (bytes > buffer.Length)
                    {
                        buffer = new byte[bytes];
                    }

                    if ((flags & BufferFlagsSilent) != 0)
                    {
                        Array.Clear(buffer, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(data, buffer, 0, bytes);
                    }

                    capture.ReleaseBuffer(count);
                    Captured?.Invoke(buffer, bytes);
                }
            }
        }
        finally
        {
            client.Stop();
            Marshal.ReleaseComObject(capture);
            Marshal.ReleaseComObject(client);
        }
    }

    private static IAudioClient Activate(int processId)
    {
        var parameters = new ActivationParams { ActivationType = 1 /* process loopback */, TargetProcessId = (uint)processId, LoopbackMode = 0 /* include tree */ };
        var size = Marshal.SizeOf<ActivationParams>();
        var blob = Marshal.AllocHGlobal(size);
        var variant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(parameters, blob, false);
            for (var offset = 0; offset < 24; offset += 4)
            {
                Marshal.WriteInt32(variant, offset, 0);
            }

            Marshal.WriteInt16(variant, 0, 65); // VT_BLOB
            Marshal.WriteInt32(variant, 8, size);
            Marshal.WriteIntPtr(variant, 16, blob);

            var handler = new CompletionHandler();
            var iid = AudioClientIid;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, handler, out _));
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Windows did not start the app's audio capture.");
            }

            return handler.Client ?? throw new InvalidOperationException("Windows refused to capture this app's audio.");
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(blob);
        }
    }

    private static void Check(int hresult) => Marshal.ThrowExceptionForHR(hresult);

    [ComVisible(true)]
    internal sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public ManualResetEventSlim Done { get; } = new();
        public IAudioClient? Client { get; private set; }

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            if (operation.GetActivateResult(out var result, out var client) == 0 && result == 0)
            {
                Client = (IAudioClient)client;
            }

            Done.Set();
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [ComImport, ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormat format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
