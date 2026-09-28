using System.Runtime.InteropServices;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace Baton.Media.Playback;

/// <summary>
/// Plays the phone's audio: 48 kHz, 16-bit, stereo PCM chunks, one per stream record.
///
/// Wi-Fi delivers audio in bursts, so the queue briefly absorbs up to <see cref="MaxQueuedBuffers"/>
/// chunks (160 ms at the phone's 10 ms chunks) instead of dropping them - a drop is an audible
/// click. But after a burst the queue would otherwise stay long forever, since chunks arrive
/// exactly as fast as they play. So when it stays above <see cref="TargetQueuedBuffers"/> for
/// <see cref="TrimAfterChunks"/> chunks in a row, one chunk is skipped: latency returns to about
/// 30-60 ms with one short skip instead of a stream of clicks.
/// </summary>
public sealed unsafe class AudioStreamPlayer : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    private const int MaxQueuedBuffers = 16;
    private const int TargetQueuedBuffers = 6;
    private const int TrimAfterChunks = 50;
    private const int PrebufferBuffers = 3;

    // Twice the queue cap: a slot is only rewritten after at least MaxQueuedBuffers later
    // submissions, by which point XAudio2 has finished with it.
    private const int SlotCount = MaxQueuedBuffers * 2;
    private const int SlotBytes = 64 * 1024;

    private readonly IXAudio2 _engine;
    private readonly IXAudio2MasteringVoice _master;
    private readonly IXAudio2SourceVoice _voice;
    private readonly byte*[] _slots = new byte*[SlotCount];
    private int _nextSlot;
    private int _chunksAboveTarget;
    private bool _started;
    private bool _disposed;

    public AudioStreamPlayer()
    {
        _engine = XAudio2.XAudio2Create();
        _master = _engine.CreateMasteringVoice();
        _voice = _engine.CreateSourceVoice(new WaveFormat(SampleRate, 16, Channels));
        for (var index = 0; index < SlotCount; index++)
        {
            _slots[index] = (byte*)NativeMemory.Alloc(SlotBytes);
        }
    }

    public long ChunksPlayed { get; private set; }

    public long ChunksDropped { get; private set; }

    /// <summary>Queues one chunk. Called from a single reader thread.</summary>
    public void Submit(ReadOnlySpan<byte> pcm)
    {
        if (_disposed || pcm.IsEmpty || pcm.Length > SlotBytes)
        {
            return;
        }

        var queued = (int)_voice.State.BuffersQueued;
        if (queued >= MaxQueuedBuffers)
        {
            ChunksDropped++;
            return;
        }

        _chunksAboveTarget = queued > TargetQueuedBuffers ? _chunksAboveTarget + 1 : 0;
        if (_chunksAboveTarget >= TrimAfterChunks)
        {
            _chunksAboveTarget = 0;
            ChunksDropped++;
            return;
        }

        // An underrun (the network stalled and the queue ran dry) rebuilds the prebuffer, so
        // playback resumes steadily instead of stuttering chunk by chunk.
        if (_started && queued == 0)
        {
            _voice.Stop();
            _started = false;
        }

        var slot = _slots[_nextSlot];
        _nextSlot = (_nextSlot + 1) % SlotCount;
        pcm.CopyTo(new Span<byte>(slot, pcm.Length));
        _voice.SubmitSourceBuffer(new AudioBuffer((nint)slot, (uint)pcm.Length, BufferFlags.None));
        ChunksPlayed++;

        if (!_started && queued + 1 >= PrebufferBuffers)
        {
            _voice.Start();
            _started = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _voice.Stop();
        _voice.FlushSourceBuffers();
        _voice.DestroyVoice();
        _voice.Dispose();
        _master.DestroyVoice();
        _master.Dispose();
        _engine.Dispose();

        // The voice is destroyed, so no buffer can still be read.
        for (var index = 0; index < SlotCount; index++)
        {
            NativeMemory.Free(_slots[index]);
        }
    }
}
