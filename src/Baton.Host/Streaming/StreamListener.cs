using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Baton.Host.Streaming;

/// <summary>
/// Accepts media stream sockets on <see cref="Port"/>.
///
/// This is a raw TLS socket rather than another WebSocket route on purpose. The control channel's
/// reader accumulates whole text messages into a 12 MiB buffer before parsing, which is the
/// opposite of what a decoder wants, and 60 fps of video on that socket would head-of-line block
/// clipboard and notification traffic behind it. One framing implementation also has to serve the
/// ADB reverse tunnel, which is a plain TCP stream with no WebSocket anywhere.
/// </summary>
public sealed class StreamListener : IHostedService, IAsyncDisposable
{
    public static int Port => HostIdentity.StreamPort;

    /// <summary>A socket that connects and then says nothing is a resource leak waiting to happen.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly HostIdentity _identity;
    private readonly StreamTicketStore _tickets;
    private readonly DiagnosticsLog _diagnostics;
    private readonly ILogger<StreamListener> _logger;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public StreamListener(
        HostIdentity identity,
        StreamTicketStore tickets,
        DiagnosticsLog diagnostics,
        ILogger<StreamListener> logger)
    {
        _identity = identity;
        _tickets = tickets;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    /// <summary>Raised on the accept loop's thread once a socket has presented a valid ticket.</summary>
    public event Func<StreamConnection, CancellationToken, Task>? ConnectionAccepted;

    public bool IsListening => _listener is not null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();

        // Frames are latency-sensitive and already sized by the encoder; letting Nagle coalesce
        // them would add up to a frame of delay for no benefit.
        listener.Server.NoDelay = true;

        _listener = listener;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);

        _diagnostics.Record(DiagnosticsCategory.Stream, "stream.listener.started", $"port {Port}");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        _listener?.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // The loop is bound to the cancelled token; a slow exit must not block shutdown.
            }
        }

        _acceptLoop = null;
        _cts.Dispose();
        _cts = null;

        _diagnostics.Record(DiagnosticsCategory.Stream, "stream.listener.stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            // One bad peer must not stall the next one, so each socket is admitted on its own task.
            _ = Task.Run(() => AdmitAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task AdmitAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        Stream stream = client.GetStream();

        try
        {
            client.NoDelay = true;

            // Every stream socket is pinned TLS, exactly like the control channel, including the
            // emulator's, which arrives from loopback.
            var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _identity.Certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                cancellationToken);
            stream = tls;

            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(HandshakeTimeout);

            var handshakeBytes = new byte[StreamFraming.HandshakeBytes];
            await StreamFraming.ReadExactlyAsync(stream, handshakeBytes, handshakeTimeout.Token);

            var handshake = StreamFraming.DecodeHandshake(handshakeBytes);
            var ticket = _tickets.Redeem(handshake.Ticket, handshake.Kind, handshake.StreamId);
            if (ticket is null)
            {
                _diagnostics.Record(
                    DiagnosticsCategory.Stream,
                    "stream.rejected",
                    $"{handshake.Kind} stream {handshake.StreamId} presented an unusable ticket.",
                    severity: DiagnosticsSeverity.Warning);
                await stream.DisposeAsync();
                client.Dispose();
                return;
            }

            var connection = new StreamConnection(stream, client, ticket, remote, isEncrypted: true);
            _diagnostics.Record(
                DiagnosticsCategory.Stream,
                "stream.accepted",
                $"{ticket.Kind} stream {ticket.StreamId}",
                ticket.DeviceId);

            var handler = ConnectionAccepted;
            if (handler is null)
            {
                await connection.DisposeAsync();
                return;
            }

            await handler(connection, cancellationToken);
        }
        catch (Exception ex)
        {
            _diagnostics.Record(
                DiagnosticsCategory.Stream,
                "stream.admit.failed",
                ex.Message,
                severity: DiagnosticsSeverity.Warning);
            _logger.LogDebug(ex, "Stream socket from {Remote} was not admitted.", remote);

            await stream.DisposeAsync();
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
}
