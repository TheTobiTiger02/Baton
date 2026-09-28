using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Protocol;
using Microsoft.AspNetCore.Http;

namespace Baton.Host;

/// <summary>
/// One phone's WebSocket, from accept to close. Nothing but pairing and the challenge proof is
/// accepted until the socket is authenticated; after that, session housekeeping is handled here and
/// every other message goes to <see cref="BatonHost.MessageReceived"/>.
/// </summary>
internal sealed class SessionHandler(BatonHost host)
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);
    private const int MaxInboundMessageBytes = 4 * 1024 * 1024;

    public async Task HandleAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var cancellation = context.RequestAborted;
        var connection = new Connection(new SessionChallengePayload(
            Guid.NewGuid().ToString("N"),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            host.Identity.HostId,
            host.Identity.PcName,
            DateTimeOffset.UtcNow.Add(ChallengeLifetime)));
        await SendDirectAsync(socket, MessageTypes.SessionChallenge, connection.Challenge, cancellation);

        try
        {
            while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(socket, cancellation);
                if (json is null)
                {
                    break;
                }

                if (connection.DeviceId is not null)
                {
                    host.Sessions.MarkActivity(connection.DeviceId, socket, connection.Generation);
                }

                await RouteAsync(socket, json, connection, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (WebSocketException ex)
        {
            host.Diagnostics.Record(DiagnosticsCategory.Transport, "session.dropped", ex.Message, connection.DeviceId);
        }
        finally
        {
            if (connection.DeviceId is not null && host.Sessions.Detach(connection.DeviceId, socket))
            {
                host.Diagnostics.Record(DiagnosticsCategory.Transport, "session.closed", deviceId: connection.DeviceId);
                host.OnDeviceDisconnected(connection.DeviceId);
            }
        }
    }

    private async Task RouteAsync(WebSocket socket, string json, Connection connection, CancellationToken cancellation)
    {
        Envelope envelope;
        try
        {
            envelope = Json.Deserialize(json);
        }
        catch (JsonException ex)
        {
            await SendErrorAsync(socket, "bad_json", ex.Message, null, cancellation);
            return;
        }

        if (Json.Validate(envelope) is { } invalid)
        {
            await SendErrorAsync(socket, "invalid_envelope", invalid, envelope.Id, cancellation);
            return;
        }

        try
        {
            switch (envelope.Type)
            {
                case MessageTypes.PairHello:
                    await HandlePairHelloAsync(socket, envelope, connection, cancellation);
                    return;
                case MessageTypes.SessionAuthenticate:
                    await HandleAuthenticateAsync(socket, envelope, connection, cancellation);
                    return;
            }

            if (connection.DeviceId is null)
            {
                await SendErrorAsync(socket, "authentication_required",
                    "Pairing or session authentication must succeed first.", envelope.Id, cancellation);
                return;
            }

            if (!string.Equals(connection.DeviceId, envelope.DeviceId, StringComparison.Ordinal))
            {
                await SendErrorAsync(socket, "device_id_mismatch",
                    "An authenticated socket cannot speak for another device.", envelope.Id, cancellation);
                return;
            }

            switch (envelope.Type)
            {
                case MessageTypes.HeartbeatPing:
                    await SendDirectAsync(socket, MessageTypes.HeartbeatPong,
                        envelope.Read<HeartbeatPayload>() ?? new HeartbeatPayload(DateTimeOffset.UtcNow), cancellation);
                    break;
                case MessageTypes.HeartbeatPong:
                    break;
                case MessageTypes.DeviceInfo:
                {
                    var info = envelope.ReadRequired<DeviceInfoPayload>();
                    host.Devices.UpdateInfo(connection.DeviceId, info);
                    if (info is { ScreenWidth: { } screenWidth, ScreenHeight: { } screenHeight })
                    {
                        host.WindowStreams.SetScreen(connection.DeviceId, screenWidth, screenHeight);
                    }

                    break;
                }

                case MessageTypes.MediaChannel:
                    await SendDirectAsync(socket, MessageTypes.MediaChannel, host.MediaChannels.Issue(connection.DeviceId), cancellation);
                    break;
                case MessageTypes.StreamControl:
                    host.WindowStreams.Control(connection.DeviceId, envelope.ReadRequired<StreamControlPayload>());
                    break;
                case MessageTypes.DeviceForget:
                    host.Devices.Revoke(connection.DeviceId);
                    host.Diagnostics.Record(DiagnosticsCategory.Pairing, "device.forgotten.by.phone", deviceId: connection.DeviceId);
                    await SendDirectAsync(socket, MessageTypes.DeviceForgotten, new DeviceForgetPayload("Forgotten on the phone."), cancellation);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Forgotten", cancellation);
                    break;
                default:
                    await host.DispatchAsync(connection.DeviceId, envelope);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            host.Diagnostics.Record(DiagnosticsCategory.Transport, "payload.error", $"{envelope.Type}: {ex.Message}",
                connection.DeviceId, DiagnosticsSeverity.Warning);
            await SendErrorAsync(socket, "payload_error", ex.Message, envelope.Id, cancellation);
        }
    }

    private async Task HandlePairHelloAsync(WebSocket socket, Envelope envelope, Connection connection, CancellationToken cancellation)
    {
        if (connection.DeviceId is not null)
        {
            await SendErrorAsync(socket, "already_authenticated", "This socket already owns a session.", envelope.Id, cancellation);
            return;
        }

        var hello = envelope.ReadRequired<PairHelloPayload>();
        var result = !string.IsNullOrWhiteSpace(hello.HostId) && hello.HostId != host.Identity.HostId
            ? new PairingResult(false, hello.DeviceId, null,
                "This pairing code belongs to a different PC.", "host_identity_mismatch")
            : host.Devices.ConfirmPairing(hello);

        host.Diagnostics.Record(DiagnosticsCategory.Pairing, result.Accepted ? "pair.accepted" : "pair.rejected",
            result.Reason, result.DeviceId, result.Accepted ? DiagnosticsSeverity.Info : DiagnosticsSeverity.Warning);
        await SendDirectAsync(socket, MessageTypes.PairConfirm, new PairConfirmPayload(
            result.Accepted,
            result.DeviceId,
            result.TrustKey,
            result.Reason,
            result.ErrorCode,
            host.Identity.HostId,
            host.Identity.PcName,
            host.Identity.CertificateFingerprint), cancellation);

        if (result.Accepted)
        {
            await ClaimAsync(socket, connection, result.DeviceId, cancellation);
        }
    }

    private async Task HandleAuthenticateAsync(WebSocket socket, Envelope envelope, Connection connection, CancellationToken cancellation)
    {
        var payload = envelope.ReadRequired<SessionAuthenticatePayload>();
        var challenge = connection.Challenge;
        if (connection.DeviceId is not null
            || challenge.ExpiresAt <= DateTimeOffset.UtcNow
            || payload.ChallengeId != challenge.ChallengeId
            || !host.Devices.AuthenticateProof(payload.DeviceId, challenge.ChallengeId, challenge.Nonce, host.Identity.HostId, payload.Proof))
        {
            host.Diagnostics.Record(DiagnosticsCategory.Pairing, "session.rejected", deviceId: payload.DeviceId,
                severity: DiagnosticsSeverity.Warning);

            // An unknown device means this PC forgot it; say so, so the phone stops retrying and
            // offers to pair again instead of looping on a dead trust key.
            if (host.Devices.GetDevice(payload.DeviceId) is null)
            {
                await SendDirectAsync(socket, MessageTypes.DeviceForgotten, new DeviceForgetPayload("This PC no longer trusts the phone."), cancellation);
            }

            await SendErrorAsync(socket, "authentication_rejected", "The session proof was rejected.", envelope.Id, cancellation);
            return;
        }

        await ClaimAsync(socket, connection, payload.DeviceId, cancellation);
    }

    private async Task ClaimAsync(WebSocket socket, Connection connection, string deviceId, CancellationToken cancellation)
    {
        connection.DeviceId = deviceId;
        connection.Generation = host.Sessions.Attach(deviceId, socket);
        await SendDirectAsync(socket, MessageTypes.SessionReady, new SessionReadyPayload(
            deviceId,
            $"{deviceId}:{connection.Generation}",
            host.Identity.HostId,
            host.Identity.PcName), cancellation);

        // Every address this PC answers on, so the phone can find it again after the LAN changes.
        await WebSocketSessionHub.SendAsync(socket, host.Endpoints.CreateEnvelope(), cancellation);
        host.Diagnostics.Record(DiagnosticsCategory.Transport, "session.ready", deviceId: deviceId);
        host.OnDeviceConnected(deviceId);
    }

    private Task SendDirectAsync<T>(WebSocket socket, string type, T payload, CancellationToken cancellation) =>
        WebSocketSessionHub.SendAsync(socket, Envelope.Create(type, host.Identity.HostId, payload), cancellation);

    private Task SendErrorAsync(WebSocket socket, string code, string message, string? correlationId, CancellationToken cancellation) =>
        SendDirectAsync(socket, MessageTypes.Error, new ErrorPayload(code, message, correlationId), cancellation);

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellation);
                }

                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException("Only text protocol messages are supported.");
            }

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > MaxInboundMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large.", cancellation);
                return null;
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            }
        }
    }

    private sealed class Connection(SessionChallengePayload challenge)
    {
        public SessionChallengePayload Challenge { get; } = challenge;
        public string? DeviceId { get; set; }
        public long Generation { get; set; }
    }
}
