using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;

namespace Baton.Media;

/// <summary>
/// The live sound of a stream or mirror as Opus: 48 kHz stereo 16-bit PCM in, one packet per
/// 20 ms out. At 160 kbit/s it is indistinguishable from the 1.5 Mbit/s PCM it replaces, and the
/// low-delay mode keeps the sound in step with the picture. Android's MediaCodec is the other end.
/// <para>
/// Only for links away from home (Tailscale, mobile data). On the home Wi-Fi the bandwidth is free
/// and the radio stays awake for the packets either way, while every packet costs the phone's
/// MediaCodec several calls across processes: there PCM is cheaper for its battery.
/// </para>
/// </summary>
public static class OpusAudio
{
    /// <summary>The name both sides announce when they can encode and decode it.</summary>
    public const string Codec = "opus";

    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int Bitrate = 160_000;

    /// <summary>20 ms: the packet length, and so the delay each packet adds.</summary>
    public const int FrameSamples = SampleRate / 50;

    /// <summary>The longest packet Opus produces: 120 ms.</summary>
    public const int MaxFrameSamples = SampleRate * 120 / 1000;

    internal const int FrameBytes = FrameSamples * Channels * sizeof(short);
}

/// <summary>Collects PCM as it is captured and hands out a packet for every full 20 ms.</summary>
public sealed class OpusAudioEncoder : IDisposable
{
    private readonly IOpusEncoder _encoder;
    private readonly byte[] _pending = new byte[OpusAudio.FrameBytes];
    private readonly byte[] _packet = new byte[1500];
    private int _filled;

    public OpusAudioEncoder()
    {
        _encoder = OpusCodecFactory.CreateEncoder(OpusAudio.SampleRate, OpusAudio.Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _encoder.Bitrate = OpusAudio.Bitrate;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
    }

    /// <summary>Feeds little-endian PCM; <paramref name="onPacket"/> gets each finished packet.</summary>
    public void Encode(ReadOnlySpan<byte> pcm, Action<byte[]> onPacket)
    {
        while (!pcm.IsEmpty)
        {
            var take = Math.Min(OpusAudio.FrameBytes - _filled, pcm.Length);
            pcm[..take].CopyTo(_pending.AsSpan(_filled));
            _filled += take;
            pcm = pcm[take..];
            if (_filled < OpusAudio.FrameBytes)
            {
                return;
            }

            _filled = 0;
            var length = _encoder.Encode(MemoryMarshal.Cast<byte, short>(_pending), OpusAudio.FrameSamples, _packet, _packet.Length);
            if (length > 0)
            {
                onPacket(_packet[..length]);
            }
        }
    }

    public void Dispose() => _encoder.Dispose();
}

/// <summary>Turns each Opus packet back into 16-bit stereo PCM.</summary>
public sealed class OpusAudioDecoder : IDisposable
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(OpusAudio.SampleRate, OpusAudio.Channels);
    private readonly short[] _samples = new short[OpusAudio.MaxFrameSamples * OpusAudio.Channels];

    /// <summary>The PCM in one packet; empty when the packet is unreadable.</summary>
    public byte[] Decode(ReadOnlySpan<byte> packet)
    {
        int frames;
        try
        {
            frames = _decoder.Decode(packet, _samples, OpusAudio.MaxFrameSamples, false);
        }
        catch (Exception ex) when (ex is OpusException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return [];
        }

        return frames <= 0 ? [] : MemoryMarshal.AsBytes(_samples.AsSpan(0, frames * OpusAudio.Channels)).ToArray();
    }

    public void Dispose() => _decoder.Dispose();
}
