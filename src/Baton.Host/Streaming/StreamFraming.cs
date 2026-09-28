using System.Buffers.Binary;

namespace Baton.Host.Streaming;

public enum StreamKind
{
    Video = 1,
    Audio = 2,
    Control = 3,
    Microphone = 4,

    /// <summary>A PC-to-phone file: raw bytes, no records, exactly the announced length.</summary>
    File = 5,

    /// <summary>The persistent, multiplexed media channel; see <see cref="StreamChannel"/>.</summary>
    Media = 6
}

/// <summary>What a record on the media channel carries. Stored in the header's second byte.</summary>
public enum StreamChannel : byte
{
    Video = 0,
    Audio = 1,
    Control = 2,

    /// <summary>JSON about the stream: its format when it starts or changes size.</summary>
    Meta = 3,

    KeepAlive = 255
}

[Flags]
public enum StreamRecordFlags
{
    None = 0,
    CodecConfig = 1 << 0,
    Keyframe = 1 << 1,
    FormatChange = 1 << 2
}

public readonly record struct StreamHandshake(StreamKind Kind, int Version, byte[] Ticket, ulong StreamId);

public readonly record struct StreamRecordHeader(
    StreamRecordFlags Flags,
    int Sequence,
    long PresentationTimeUs,
    int Length,
    StreamChannel Channel = StreamChannel.Video)
{
    public bool IsCodecConfig => Flags.HasFlag(StreamRecordFlags.CodecConfig);

    public bool IsKeyframe => Flags.HasFlag(StreamRecordFlags.Keyframe);

    public bool IsFormatChange => Flags.HasFlag(StreamRecordFlags.FormatChange);
}

/// <summary>
/// Desktop twin of <c>dev.baton.android.stream.StreamProtocol</c>. Byte-identical by
/// contract; a parity test on each side fails if one is edited alone.
///
/// <code>
/// Handshake (client -> server), 48 bytes, big endian:
///   magic    u32  = 0x42544E31 ("BTN1")
///   kind     u8   1 video | 2 audio | 3 control | 4 mic
///   version  u8   = 1
///   reserved u16
///   ticket   u8[32]
///   streamId u64
///
/// Record header, 16 bytes, big endian:
///   flags    u8   bit0 codec config, bit1 keyframe, bit2 format change
///   channel  u8   on the media channel: 0 video, 1 audio, 2 control, 3 meta, 255 keepalive
///   sequence u16  wraps; gaps mean loss
///   ptsUs    u64
///   length   u32
/// </code>
/// </summary>
public static class StreamFraming
{
    public const uint Magic = 0x42544E31; // "BTN1"
    public const int Version = 1;

    public const int HandshakeBytes = 48;
    public const int HeaderBytes = 16;
    public const int TicketBytes = 32;

    /// <summary>Refuses anything absurd before allocating for it.</summary>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    public static byte[] EncodeHandshake(StreamKind kind, ReadOnlySpan<byte> ticket, ulong streamId)
    {
        if (ticket.Length != TicketBytes)
        {
            throw new ArgumentException($"Ticket must be {TicketBytes} bytes, was {ticket.Length}.", nameof(ticket));
        }

        var bytes = new byte[HandshakeBytes];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), Magic);
        bytes[4] = (byte)kind;
        bytes[5] = Version;
        // bytes[6..8] reserved
        ticket.CopyTo(bytes.AsSpan(8, TicketBytes));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(40, 8), streamId);
        return bytes;
    }

    public static StreamHandshake DecodeHandshake(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HandshakeBytes)
        {
            throw new ArgumentException($"Handshake must be {HandshakeBytes} bytes.", nameof(bytes));
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]);
        if (magic != Magic)
        {
            throw new InvalidDataException($"Not a Baton stream (magic 0x{magic:X8}).");
        }

        var kind = (StreamKind)bytes[4];
        int version = bytes[5];
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported stream version {version}.");
        }

        var ticket = bytes.Slice(8, TicketBytes).ToArray();
        var streamId = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(40, 8));
        return new StreamHandshake(kind, version, ticket, streamId);
    }

    public static byte[] EncodeHeader(StreamRecordFlags flags, int sequence, long presentationTimeUs, int length, StreamChannel channel = StreamChannel.Video)
    {
        if (length is < 0 or > MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"Payload length {length} is out of range.");
        }

        var bytes = new byte[HeaderBytes];
        bytes[0] = (byte)flags;
        bytes[1] = (byte)channel;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2, 2), (ushort)sequence);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(4, 8), presentationTimeUs);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12, 4), length);
        return bytes;
    }

    public static StreamRecordHeader DecodeHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes)
        {
            throw new ArgumentException($"Header must be {HeaderBytes} bytes.", nameof(bytes));
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(12, 4));
        if (length is < 0 or > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Payload length {length} is out of range.");
        }

        return new StreamRecordHeader(
            (StreamRecordFlags)bytes[0],
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2, 2)),
            BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(4, 8)),
            length,
            (StreamChannel)bytes[1]);
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes; a short read is a truncated stream.</summary>
    public static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var chunk = await stream.ReadAsync(destination[read..], cancellationToken);
            if (chunk == 0)
            {
                throw new EndOfStreamException($"Stream ended after {read} of {destination.Length} bytes.");
            }

            read += chunk;
        }
    }

    public static async Task<StreamRecordHeader> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderBytes];
        await ReadExactlyAsync(stream, header, cancellationToken);
        return DecodeHeader(header);
    }
}
