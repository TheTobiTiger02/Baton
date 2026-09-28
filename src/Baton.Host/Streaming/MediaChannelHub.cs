using System.Collections.Concurrent;
using Baton.Protocol;

namespace Baton.Host.Streaming;

/// <summary>
/// One persistent, pinned TLS socket per phone for everything that is not a protocol message:
/// window video and sound to the phone, the phone's screen to the PC, and input both ways. It is
/// opened once per session, so starting a stream never waits for a handshake.
///
/// Writes go through one writer thread per phone. Video is the only thing allowed to be dropped:
/// when the phone falls behind, delta frames are skipped until the next keyframe instead of
/// letting latency pile up in socket buffers.
/// </summary>
public sealed class MediaChannelHub(StreamTicketStore tickets, DiagnosticsLog diagnostics) : IDisposable
{
    private const int MaxQueuedVideo = 4;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private long _nextStreamId = Environment.TickCount64 << 8;

    /// <summary>A record from a phone: (deviceId, header, payload). Raised on the channel's reader thread.</summary>
    public event Action<string, StreamRecordHeader, byte[]>? RecordReceived;

    /// <summary>A phone's channel opened (true) or closed (false).</summary>
    public event Action<string, bool>? ChannelChanged;

    /// <summary>The phone fell behind and dropped video; the stream should send a keyframe soon.</summary>
    public event Action<string>? KeyframeNeeded;

    public bool IsOpen(string deviceId) => _channels.ContainsKey(deviceId);

    /// <summary>The phone's channel comes over Tailscale rather than the home network.</summary>
    public bool IsRemote(string deviceId) =>
        _channels.TryGetValue(deviceId, out var channel) && BitrateLadder.IsRemote(channel.RemoteAddress);

    /// <summary>A single-use ticket the phone presents to open its channel.</summary>
    public MediaChannelPayload Issue(string deviceId)
    {
        var streamId = (ulong)Interlocked.Increment(ref _nextStreamId);
        var ticket = tickets.Issue(deviceId, "media", streamId, StreamKind.Media);
        return new MediaChannelPayload(Convert.ToBase64String(ticket.Value), streamId, HostIdentity.StreamPort);
    }

    public async Task AcceptAsync(StreamConnection connection, CancellationToken cancellationToken)
    {
        var channel = new Channel(this, connection);
        if (_channels.TryGetValue(connection.DeviceId, out var previous))
        {
            previous.Close();
        }

        _channels[connection.DeviceId] = channel;
        diagnostics.Record(DiagnosticsCategory.Stream, "media.channel.open", deviceId: connection.DeviceId);
        ChannelChanged?.Invoke(connection.DeviceId, true);
        try
        {
            while (await connection.ReadRecordAsync(cancellationToken) is { } record)
            {
                if (record.Header.Channel != StreamChannel.KeepAlive)
                {
                    RecordReceived?.Invoke(connection.DeviceId, record.Header, record.Payload);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
        }
        finally
        {
            channel.Close();
            if (_channels.TryGetValue(connection.DeviceId, out var current) && ReferenceEquals(current, channel))
            {
                _channels.TryRemove(connection.DeviceId, out _);
                diagnostics.Record(DiagnosticsCategory.Stream, "media.channel.closed", deviceId: connection.DeviceId);
                ChannelChanged?.Invoke(connection.DeviceId, false);
            }
        }
    }

    /// <summary>Queues a record for a phone. False when its channel is not open.</summary>
    public bool Send(string deviceId, StreamChannel channel, StreamRecordFlags flags, long presentationTimeUs, byte[] payload)
    {
        if (!_channels.TryGetValue(deviceId, out var target))
        {
            return false;
        }

        target.Enqueue(new Outgoing(channel, flags, presentationTimeUs, payload));
        return true;
    }

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Close();
        }

        _channels.Clear();
    }

    private sealed record Outgoing(StreamChannel Channel, StreamRecordFlags Flags, long PresentationTimeUs, byte[] Payload);

    private sealed class Channel
    {
        private readonly MediaChannelHub _owner;
        private readonly StreamConnection _connection;
        private readonly BlockingCollection<Outgoing> _queue = new();
        private readonly CancellationTokenSource _stop = new();
        private int _queuedVideo;
        private bool _awaitingKeyframe;
        private int _closed;

        public System.Net.IPAddress? RemoteAddress => _connection.RemoteEndPoint?.Address;

        public Channel(MediaChannelHub owner, StreamConnection connection)
        {
            _owner = owner;
            _connection = connection;
            new Thread(Write) { IsBackground = true, Name = $"Baton media channel {connection.DeviceId}" }.Start();
        }

        public void Enqueue(Outgoing record)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return;
            }

            if (record.Channel == StreamChannel.Video)
            {
                var isKey = (record.Flags & (StreamRecordFlags.Keyframe | StreamRecordFlags.CodecConfig)) != 0;
                lock (_queue)
                {
                    if (!isKey && (_awaitingKeyframe || _queuedVideo >= MaxQueuedVideo))
                    {
                        if (!_awaitingKeyframe)
                        {
                            _awaitingKeyframe = true;
                            _owner.KeyframeNeeded?.Invoke(_connection.DeviceId);
                        }

                        return;
                    }

                    if ((record.Flags & StreamRecordFlags.Keyframe) != 0)
                    {
                        _awaitingKeyframe = false;
                    }

                    _queuedVideo++;
                }
            }

            try
            {
                _queue.Add(record);
            }
            catch (InvalidOperationException)
            {
                // Closed while adding.
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            _stop.Cancel();
            _queue.CompleteAdding();
            _ = _connection.DisposeAsync();
        }

        private void Write()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    if (!_queue.TryTake(out var record, (int)KeepAliveInterval.TotalMilliseconds, _stop.Token))
                    {
                        record = new Outgoing(StreamChannel.KeepAlive, StreamRecordFlags.None, 0, []);
                    }

                    if (record.Channel == StreamChannel.Video)
                    {
                        lock (_queue)
                        {
                            _queuedVideo--;
                        }
                    }

                    _connection.WriteRecord(record.Flags, record.PresentationTimeUs, record.Payload, record.Channel);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                Close();
            }
        }
    }
}
