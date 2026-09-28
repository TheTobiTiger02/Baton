using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Baton.Protocol;

namespace Baton.Host;

public sealed record WebSocketSessionStatus(
    string SessionId,
    long Generation,
    int QueuedMessages,
    long DroppedMessages,
    DateTimeOffset AttachedAt,
    DateTimeOffset LastActivityAt,
    int QueuedTelemetry = 0,
    long DroppedTelemetry = 0);

/// <summary>
/// Outbound message classes. Telemetry-shaped messages are supersedable: the newest one is the
/// only one worth delivering, and a burst of them must never evict a command.
/// </summary>
public enum SendPriority
{
    Control,
    Telemetry
}

public sealed class WebSocketSessionHub : IDisposable
{
    public const int QueueCapacity = 128;
    public const int TelemetryQueueCapacity = 64;

    private static readonly ConditionalWeakTable<WebSocket, SemaphoreSlim> SocketWriters = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _generation;
    private bool _disposed;

    public IReadOnlyList<string> ConnectedDeviceIds
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Keys.Order(StringComparer.Ordinal).ToArray();
            }
        }
    }

    public long Attach(string deviceId, WebSocket socket)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return 0;
        }

        SessionEntry? replaced = null;
        SessionEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.TryGetValue(deviceId, out var current)
                && ReferenceEquals(current.Socket, socket))
            {
                return current.Generation;
            }

            entry = new SessionEntry(
                deviceId,
                Interlocked.Increment(ref _generation),
                socket,
                OnWriterStopped);
            if (_sessions.TryGetValue(deviceId, out current))
            {
                replaced = current;
            }

            _sessions[deviceId] = entry;
        }

        replaced?.Stop();
        entry.Start();
        return entry.Generation;
    }

    public bool Detach(string deviceId, WebSocket? socket = null)
    {
        SessionEntry? removed = null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(deviceId, out var current)
                || (socket is not null && !ReferenceEquals(current.Socket, socket)))
            {
                return false;
            }

            _sessions.Remove(deviceId);
            removed = current;
        }

        removed.Stop();
        return true;
    }

    public bool IsCurrent(string deviceId, WebSocket socket, long generation)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(deviceId, out var entry)
                && entry.Generation == generation
                && ReferenceEquals(entry.Socket, socket);
        }
    }

    public bool MarkActivity(
        string deviceId,
        WebSocket socket,
        long generation,
        DateTimeOffset? observedAt = null)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(deviceId, out var entry)
                || entry.Generation != generation
                || !ReferenceEquals(entry.Socket, socket))
            {
                return false;
            }

            entry.Touch(observedAt ?? DateTimeOffset.UtcNow);
            return true;
        }
    }

    public async Task<int> CloseStaleAsync(
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken)
    {
        SessionEntry[] stale;
        lock (_gate)
        {
            stale = _sessions.Values
                .Where(entry => entry.LastActivityAt <= staleBefore)
                .ToArray();
            foreach (var entry in stale)
            {
                if (_sessions.TryGetValue(entry.SessionId, out var current)
                    && current.Generation == entry.Generation
                    && ReferenceEquals(current.Socket, entry.Socket))
                {
                    _sessions.Remove(entry.SessionId);
                }
            }
        }

        foreach (var entry in stale)
        {
            entry.Stop();
            await entry.CloseStaleAsync(cancellationToken);
        }

        return stale.Length;
    }

    public bool HasSession(string deviceId)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(deviceId);
        }
    }

    public IReadOnlyList<WebSocketSessionStatus> GetStatuses()
    {
        lock (_gate)
        {
            return _sessions.Values
                .Select(entry => entry.GetStatus())
                .OrderBy(status => status.SessionId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public Task<bool> SendAsync(
        string deviceId,
        Envelope envelope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionEntry? entry;
        lock (_gate)
        {
            _sessions.TryGetValue(deviceId, out entry);
        }

        return Task.FromResult(entry?.TryEnqueue(envelope, ClassifyPriority(envelope.Type)) == true);
    }

    public static async Task SendAsync(
        WebSocket socket,
        Envelope envelope,
        CancellationToken cancellationToken)
    {
        var writer = SocketWriters.GetValue(socket, _ => new SemaphoreSlim(1, 1));
        await writer.WaitAsync(cancellationToken);
        try
        {
            await SendCoreAsync(socket, envelope, cancellationToken);
        }
        finally
        {
            writer.Release();
        }
    }

    /// <summary>
    /// A message is telemetry when a newer copy supersedes it: activity snapshots are resent in
    /// full on every change, so losing one costs nothing but a moment of staleness. Handoff
    /// commands stay Control because dropping one is a visible failure.
    /// </summary>
    public static SendPriority ClassifyPriority(string messageType) =>
        messageType is MessageTypes.Peers
            ? SendPriority.Telemetry
            : SendPriority.Control;

    public void Dispose()
    {
        SessionEntry[] entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Stop();
        }
    }

    private static async Task SendCoreAsync(
        WebSocket socket,
        Envelope envelope,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(envelope));
        await socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private void OnWriterStopped(SessionEntry stopped)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(stopped.SessionId, out var current)
                && current.Generation == stopped.Generation)
            {
                _sessions.Remove(stopped.SessionId);
            }
        }
    }

    private sealed class SessionEntry
    {
        private readonly Channel<Envelope> _outbound = Channel.CreateBounded<Envelope>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        private readonly Channel<Envelope> _telemetry = Channel.CreateBounded<Envelope>(
            new BoundedChannelOptions(TelemetryQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        // Signals "some queue has work". Over-counting only costs an empty wake-up, so it is
        // released on every accepted write and never adjusted when a stale sample is displaced.
        private readonly SemaphoreSlim _pending = new(0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Action<SessionEntry> _onStopped;
        private int _queuedMessages;
        private long _droppedMessages;
        private int _queuedTelemetry;
        private long _droppedTelemetry;
        private long _lastActivityUtcTicks;
        private int _started;

        public SessionEntry(
            string sessionId,
            long generation,
            WebSocket socket,
            Action<SessionEntry> onStopped)
        {
            SessionId = sessionId;
            Generation = generation;
            Socket = socket;
            _onStopped = onStopped;
            AttachedAt = DateTimeOffset.UtcNow;
            _lastActivityUtcTicks = AttachedAt.UtcTicks;
        }

        public string SessionId { get; }
        public long Generation { get; }
        public WebSocket Socket { get; }
        public DateTimeOffset AttachedAt { get; }
        public DateTimeOffset LastActivityAt =>
            new(Interlocked.Read(ref _lastActivityUtcTicks), TimeSpan.Zero);

        public void Touch(DateTimeOffset observedAt) =>
            Interlocked.Exchange(ref _lastActivityUtcTicks, observedAt.UtcTicks);

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _ = RunWriterAsync();
            }
        }

        public bool TryEnqueue(Envelope envelope, SendPriority priority)
        {
            if (_stop.IsCancellationRequested)
            {
                if (priority == SendPriority.Telemetry)
                {
                    Interlocked.Increment(ref _droppedTelemetry);
                }
                else
                {
                    Interlocked.Increment(ref _droppedMessages);
                }

                return false;
            }

            if (priority == SendPriority.Telemetry)
            {
                if (!_telemetry.Writer.TryWrite(envelope))
                {
                    Interlocked.Increment(ref _droppedTelemetry);
                    return false;
                }

                // DropOldest accepts every write; when the queue was already full it silently
                // displaced the stale sample, which is exactly the intended behaviour.
                if (Interlocked.Increment(ref _queuedTelemetry) > TelemetryQueueCapacity)
                {
                    Interlocked.Decrement(ref _queuedTelemetry);
                    Interlocked.Increment(ref _droppedTelemetry);
                }

                ReleasePending();
                return true;
            }

            if (!_outbound.Writer.TryWrite(envelope))
            {
                Interlocked.Increment(ref _droppedMessages);
                return false;
            }

            Interlocked.Increment(ref _queuedMessages);
            ReleasePending();
            return true;
        }

        private void ReleasePending()
        {
            try
            {
                _pending.Release();
            }
            catch (ObjectDisposedException)
            {
                // The writer stopped between the cancellation check and here.
            }
        }

        public void Stop()
        {
            if (!_stop.IsCancellationRequested)
            {
                _stop.Cancel();
                _outbound.Writer.TryComplete();
                _telemetry.Writer.TryComplete();
                ReleasePending();
            }
        }

        public async Task CloseStaleAsync(CancellationToken cancellationToken)
        {
            using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            closeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await Socket.CloseOutputAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        "Session heartbeat expired.",
                        closeTimeout.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                Socket.Abort();
            }
        }

        public WebSocketSessionStatus GetStatus() => new(
            SessionId,
            Generation,
            Volatile.Read(ref _queuedMessages),
            Interlocked.Read(ref _droppedMessages),
            AttachedAt,
            LastActivityAt,
            Volatile.Read(ref _queuedTelemetry),
            Interlocked.Read(ref _droppedTelemetry));

        /// <summary>
        /// Drains control work to exhaustion before touching telemetry, so a burst of activity
        /// snapshots can never delay a handoff command behind it.
        /// </summary>
        private async Task RunWriterAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await _pending.WaitAsync(_stop.Token);

                    while (_outbound.Reader.TryRead(out var control))
                    {
                        Interlocked.Decrement(ref _queuedMessages);
                        await SendCoreAsync(Socket, control, _stop.Token);
                    }

                    if (_telemetry.Reader.TryRead(out var sample))
                    {
                        Interlocked.Decrement(ref _queuedTelemetry);
                        await SendCoreAsync(Socket, sample, _stop.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // Session teardown and network loss both end this generation's writer.
            }
            finally
            {
                _onStopped(this);
                _stop.Dispose();
            }
        }
    }
}
