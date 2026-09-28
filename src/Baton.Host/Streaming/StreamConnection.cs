using System.Net;

namespace Baton.Host.Streaming;

public sealed record StreamRecord(StreamRecordHeader Header, byte[] Payload);

/// <summary>
/// One accepted media stream: an authenticated socket plus the ticket that admitted it.
///
/// Records are read one at a time rather than buffered into a queue. The consumer - the decoder -
/// is the only thing that should decide what to do with a frame it cannot keep up with, and an
/// invisible queue in between would turn "we are behind" into "we are behind and also adding
/// latency".
/// </summary>
public sealed class StreamConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IDisposable _socket;

    internal StreamConnection(
        Stream stream,
        IDisposable socket,
        StreamTicket ticket,
        IPEndPoint? remoteEndPoint,
        bool isEncrypted)
    {
        _stream = stream;
        _socket = socket;
        Ticket = ticket;
        RemoteEndPoint = remoteEndPoint;
        IsEncrypted = isEncrypted;
    }

    public StreamTicket Ticket { get; }

    public string DeviceId => Ticket.DeviceId;

    public string SessionId => Ticket.SessionId;

    public ulong StreamId => Ticket.StreamId;

    public StreamKind Kind => Ticket.Kind;

    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// False only for the ADB reverse tunnel, which is loopback-to-loopback and already inside the
    /// USB or wireless-debugging trust boundary.
    /// </summary>
    public bool IsEncrypted { get; }

    public long RecordsRead { get; private set; }

    public long BytesRead { get; private set; }

    /// <summary>Reads the next record, or null at end of stream.</summary>
    public async Task<StreamRecord?> ReadRecordAsync(CancellationToken cancellationToken)
    {
        StreamRecordHeader header;
        try
        {
            header = await StreamFraming.ReadHeaderAsync(_stream, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var payload = new byte[header.Length];
        if (header.Length > 0)
        {
            await StreamFraming.ReadExactlyAsync(_stream, payload, cancellationToken);
        }

        RecordsRead++;
        BytesRead += StreamFraming.HeaderBytes + header.Length;
        return new StreamRecord(header, payload);
    }

    private readonly object _writeGate = new();
    private readonly int[] _sequences = new int[256];

    /// <summary>
    /// Writes one framed record synchronously. Safe from any thread; records from different
    /// writers never interleave.
    /// </summary>
    public void WriteRecord(StreamRecordFlags flags, long presentationTimeUs, ReadOnlySpan<byte> payload, StreamChannel channel = StreamChannel.Video)
    {
        lock (_writeGate)
        {
            _stream.Write(StreamFraming.EncodeHeader(flags, _sequences[(byte)channel]++, presentationTimeUs, payload.Length, channel));
            _stream.Write(payload);
            _stream.Flush();
        }
    }

    /// <summary>Sends a control-stream message. Only meaningful on a control connection.</summary>
    public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        _stream.WriteAsync(bytes, cancellationToken).AsTask();

    public Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _socket.Dispose();
    }
}
