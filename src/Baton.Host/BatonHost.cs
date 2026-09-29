using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Baton.Host.Streaming;
using Baton.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Baton.Host;

/// <summary>
/// The PC side of the link: identity, trusted phones, pinned WSS sessions and UDP discovery.
/// Runs in the desktop app's process; the app talks to it directly rather than over loopback.
/// </summary>
public sealed class BatonHost : IAsyncDisposable
{
    public const string SessionPath = "/ws/v1/session";
    public const string DiscoveryRequest = "BATON_DISCOVER_V1";

    private WebApplication? _app;
    private UdpClient? _udp;
    private CancellationTokenSource? _udpCts;

    public BatonHost(string? storageDirectory = null)
    {
        StorageDirectory = storageDirectory ?? HostIdentity.DefaultStorageDirectory;
        Identity = HostIdentity.LoadOrCreate(StorageDirectory);
        Devices = new DeviceRegistry(Path.Combine(StorageDirectory, "devices.json"));
        DeviceNames = new DeviceNames(Path.Combine(StorageDirectory, "device-names.json"));
        Endpoints = new EndpointEnumerator(Sessions, Identity, Diagnostics);
        StreamListener = new StreamListener(Identity, StreamTickets, Diagnostics,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamListener>.Instance);
        Timeline = new HandoffTimeline(Diagnostics);
        MediaChannels = new MediaChannelHub(StreamTickets, Diagnostics);
        WindowStreams = new WindowStreamService(MediaChannels, Diagnostics, Timeline);
        WindowStreams.MutedAppsPath = Path.Combine(StorageDirectory, "muted-apps.json");
        WindowStreams.RestoreMutedApps();
        StreamListener.ConnectionAccepted += (connection, cancellation) => connection.Kind == StreamKind.Media
            ? MediaChannels.AcceptAsync(connection, cancellation)
            : connection.DisposeAsync().AsTask();
    }

    public HandoffTimeline Timeline { get; }

    /// <summary>Names given to phones on this PC.</summary>
    public DeviceNames DeviceNames { get; }

    /// <summary>What to call a phone: the name given here, else the one it reports.</summary>
    public string? NameOf(string deviceId) =>
        DeviceNames.Get(deviceId) ?? Devices.GetDevice(deviceId)?.DisplayName;

    /// <summary>Each phone's persistent media socket: streams and input in both directions.</summary>
    public MediaChannelHub MediaChannels { get; }

    public StreamTicketStore StreamTickets { get; } = new();

    /// <summary>Local media files offered to phones, served at <c>/files/{token}</c>.</summary>
    public Media.LocalFiles Files { get; } = new();
    internal StreamListener StreamListener { get; }

    /// <summary>Live window streams to phones.</summary>
    public WindowStreamService WindowStreams { get; }

    public string StorageDirectory { get; }
    public HostIdentity Identity { get; }
    public DeviceRegistry Devices { get; }
    public DiagnosticsLog Diagnostics { get; } = new();
    public WebSocketSessionHub Sessions { get; } = new();
    internal EndpointEnumerator Endpoints { get; }

    /// <summary>
    /// Serves the browser extension on <c>ws://127.0.0.1:7836/browser</c>. Set before
    /// <see cref="StartAsync"/>; only loopback connections to that port ever reach it.
    /// </summary>
    public Func<HttpContext, Task>? BrowserEndpoint { get; set; }

    /// <summary>Messages from authenticated phones that are not session housekeeping.</summary>
    public event Func<string, Envelope, Task>? MessageReceived;

    /// <summary>A phone's session became ready (true) or ended (false).</summary>
    public event Action<string, bool>? DeviceConnectionChanged;

    public bool IsRunning => _app is not null;

    public bool IsConnected(string deviceId) => Sessions.HasSession(deviceId);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        HostPortPreflight.VerifyRequiredEndpointsAvailable();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, HostIdentity.WssPort, listen => listen.UseHttps(Identity.Certificate));
            options.Listen(IPAddress.Loopback, HostIdentity.BrowserPort);
        });
        builder.Services.AddSingleton(Sessions);
        builder.Services.AddHostedService<SessionStaleMonitor>();
        builder.Services.AddHostedService(_ => Endpoints);

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            KeepAliveTimeout = TimeSpan.FromSeconds(60)
        });
        var handler = new SessionHandler(this);
        app.Map(SessionPath, async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }

            await handler.HandleAsync(context);
        });

        // Local media for phones: range requests over the pinned TLS port, by secret token only.
        app.MapGet("/files/{token}", (HttpContext context, string token) =>
        {
            if (context.Connection.LocalPort != HostIdentity.WssPort || Files.Resolve(token) is not { } path)
            {
                return Results.NotFound();
            }

            return Results.File(path, Media.LocalFiles.MimeFor(path), enableRangeProcessing: true);
        });

        app.Map("/browser", async context =>
        {
            if (BrowserEndpoint is null
                || context.Connection.LocalPort != HostIdentity.BrowserPort
                || context.Connection.RemoteIpAddress is not { } remote
                || !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await BrowserEndpoint(context);
        });

        await app.StartAsync(cancellationToken);
        await StreamListener.StartAsync(cancellationToken);
        _app = app;
        StartDiscoveryResponder();
        Diagnostics.Record(DiagnosticsCategory.Transport, "host.started", $"wss {HostIdentity.WssPort}");
    }

    public async Task StopAsync()
    {
        _udpCts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        WindowStreams.Dispose();
        MediaChannels.Dispose();
        await StreamListener.StopAsync(CancellationToken.None);
        if (_app is not null)
        {
            Sessions.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Identity.Dispose();
    }

    /// <summary>Opens a five-minute pairing code and returns the QR link that carries it.</summary>
    public PairingLink OpenPairing()
    {
        var ticket = Devices.OpenPairingTicket();
        return new PairingLink(Identity.HostId, Identity.PcName, Identity.CertificateFingerprint, ticket.Code, Endpoints.Current);
    }

    public Task<bool> SendAsync<T>(string deviceId, string type, T payload) =>
        Sessions.SendAsync(deviceId, Envelope.Create(type, Identity.HostId, payload), CancellationToken.None);

    /// <summary>
    /// Sends now, or as soon as the phone is back within <paramref name="wait"/>: a phone that is
    /// reconnecting (after an update, or a Wi-Fi blip) still gets the handoff instead of losing it.
    /// </summary>
    public async Task<bool> SendWhenConnectedAsync<T>(string deviceId, string type, T payload, TimeSpan? wait = null)
    {
        if (await SendAsync(deviceId, type, payload))
        {
            return true;
        }

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChange(string id, bool isConnected)
        {
            if (id == deviceId && isConnected)
            {
                connected.TrySetResult();
            }
        }

        DeviceConnectionChanged += OnChange;
        try
        {
            if (IsConnected(deviceId) || await Task.WhenAny(connected.Task, Task.Delay(wait ?? TimeSpan.FromSeconds(10))) == connected.Task)
            {
                return await SendAsync(deviceId, type, payload);
            }

            return false;
        }
        finally
        {
            DeviceConnectionChanged -= OnChange;
        }
    }

    /// <summary>Forgets a phone here and tells it so, if it is online, so it stops reconnecting.</summary>
    public async Task ForgetAsync(string deviceId)
    {
        await SendAsync(deviceId, MessageTypes.DeviceForgotten, new DeviceForgetPayload("Forgotten on the PC."));

        // Give the writer loop a moment to flush before the session is torn down.
        await Task.Delay(300);
        Devices.Revoke(deviceId);
        Sessions.Detach(deviceId);
        Diagnostics.Record(DiagnosticsCategory.Pairing, "device.forgotten.by.pc", deviceId: deviceId);
    }

    internal async Task DispatchAsync(string deviceId, Envelope envelope)
    {
        var handlers = MessageReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (Func<string, Envelope, Task> handler in handlers.GetInvocationList())
        {
            await handler(deviceId, envelope);
        }
    }

    internal void OnDeviceConnected(string deviceId)
    {
        Devices.MarkConnected(deviceId, true);
        DeviceConnectionChanged?.Invoke(deviceId, true);
    }

    internal void OnDeviceDisconnected(string deviceId)
    {
        Devices.MarkConnected(deviceId, false);
        DeviceConnectionChanged?.Invoke(deviceId, false);
    }

    private void StartDiscoveryResponder()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true, EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, HostIdentity.DiscoveryPort));
        _udp = udp;
        _udpCts = new CancellationTokenSource();
        var token = _udpCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var request = await udp.ReceiveAsync(token);
                    if (Encoding.UTF8.GetString(request.Buffer) != DiscoveryRequest)
                    {
                        continue;
                    }

                    var reply = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryResponse(
                        Identity.HostId,
                        Identity.PcName,
                        Envelope.CurrentVersion,
                        HostIdentity.WssPort,
                        Identity.CertificateFingerprint,
                        Endpoints.Current), Json.Options);
                    await udp.SendAsync(reply, request.RemoteEndPoint, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // A reply to an unreachable phone must not stop discovery for everyone else.
                }
            }
        }, token);
    }
}
