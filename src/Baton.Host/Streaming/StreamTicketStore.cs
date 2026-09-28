using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Baton.Host.Streaming;

public sealed record StreamTicket(
    byte[] Value,
    string DeviceId,
    string SessionId,
    ulong StreamId,
    StreamKind Kind,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Single-use admission tickets for the media stream socket.
///
/// The stream socket cannot redo the pairing handshake - it carries video, not protocol envelopes -
/// so admission is delegated to the control channel, which is already authenticated and certificate
/// pinned. The host mints a ticket there, the phone presents it as the first bytes on the new
/// socket, and the ticket dies on first use. A ticket therefore proves "the peer that just
/// authenticated on the pinned socket asked for this exact stream", which is the property that
/// matters; it never travels anywhere else.
/// </summary>
public sealed class StreamTicketStore
{
    /// <summary>
    /// The ticket is minted when the PC asks for a mirror but redeemed only after a person has
    /// tapped through Android's screen-capture consent dialog, so its lifetime has to cover a human
    /// step, not just a socket connect. Thirty seconds was measured to be too short on a real
    /// phone. Five minutes matches the mirror start lease; a longer ticket costs little because it
    /// is single use, bound to one device, kind and stream, and never leaves the pinned channel.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, StreamTicket> _tickets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public StreamTicketStore(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    public int Count
    {
        get
        {
            PruneExpired();
            return _tickets.Count;
        }
    }

    public StreamTicket Issue(string deviceId, string sessionId, ulong streamId, StreamKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        PruneExpired();

        var value = RandomNumberGenerator.GetBytes(StreamFraming.TicketBytes);
        var ticket = new StreamTicket(
            value,
            deviceId,
            sessionId,
            streamId,
            kind,
            _time.GetUtcNow() + Lifetime);

        _tickets[Key(value)] = ticket;
        return ticket;
    }

    /// <summary>
    /// Consumes a ticket. Returns null when it is unknown, already used, expired, or presented for
    /// a different stream than the one it was minted for.
    /// </summary>
    public StreamTicket? Redeem(ReadOnlySpan<byte> value, StreamKind kind, ulong streamId)
    {
        if (value.Length != StreamFraming.TicketBytes)
        {
            return null;
        }

        if (!_tickets.TryRemove(Key(value), out var ticket))
        {
            return null;
        }

        if (ticket.ExpiresAt <= _time.GetUtcNow())
        {
            return null;
        }

        // Binding to kind and stream id means a ticket minted for a video stream cannot be spent
        // on a control socket, even within its lifetime.
        return ticket.Kind == kind && ticket.StreamId == streamId ? ticket : null;
    }

    /// <summary>Drops every outstanding ticket for a device, e.g. when its session ends.</summary>
    public int RevokeDevice(string deviceId)
    {
        var revoked = 0;
        foreach (var pair in _tickets)
        {
            if (string.Equals(pair.Value.DeviceId, deviceId, StringComparison.Ordinal)
                && _tickets.TryRemove(pair))
            {
                revoked++;
            }
        }

        return revoked;
    }

    private void PruneExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _tickets)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _tickets.TryRemove(pair);
            }
        }
    }

    private static string Key(ReadOnlySpan<byte> value) => Convert.ToHexString(value);
}
