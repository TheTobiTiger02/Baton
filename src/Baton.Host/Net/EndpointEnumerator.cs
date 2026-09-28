using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Baton.Protocol;

namespace Baton.Host;

/// <summary>
/// Lists every address a phone can reach this PC's secure session port on, and pushes the list to
/// connected phones whenever it changes.
///
/// UDP discovery only works on the same LAN. Tailscale addresses reach the phone this way instead:
/// sent over a live session and remembered, so a phone that leaves the Wi-Fi can still find the PC
/// through the tailnet. The certificate pin, not the address, is the PC's identity, so offering
/// more addresses never weakens trust.
/// </summary>
public sealed class EndpointEnumerator(
    WebSocketSessionHub sessions,
    HostIdentity identity,
    DiagnosticsLog diagnostics) : BackgroundService
{
    public const string KindLan = "lan";
    public const string KindTailscale = "tailscale";

    // Tailscale assigns node addresses from the CGNAT range 100.64.0.0/10.
    private static readonly IPAddress TailscalePrefix = IPAddress.Parse("100.64.0.0");
    private const int TailscalePrefixLength = 10;

    private readonly object _gate = new();
    private IReadOnlyList<HostEndpoint> _current = Enumerate();

    public IReadOnlyList<HostEndpoint> Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public Envelope CreateEnvelope() => Envelope.Create(
        MessageTypes.HostEndpoints,
        identity.HostId,
        new HostEndpointsPayload(identity.HostId, Current));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var changed = new SemaphoreSlim(0);
        void OnChanged(object? sender, EventArgs e) => changed.Release();

        NetworkChange.NetworkAddressChanged += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await changed.WaitAsync(stoppingToken);

                // Address changes arrive in bursts while an adapter comes up; settle first.
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                while (changed.CurrentCount > 0)
                {
                    await changed.WaitAsync(stoppingToken);
                }

                await RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnChanged;
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var next = Enumerate();
        lock (_gate)
        {
            if (next.SequenceEqual(_current))
            {
                return;
            }

            _current = next;
        }

        diagnostics.Record(
            DiagnosticsCategory.Transport,
            "host.endpoints.changed",
            string.Join(", ", next.Select(endpoint => $"{endpoint.Kind}:{endpoint.Host}")));

        var envelope = CreateEnvelope();
        foreach (var status in sessions.GetStatuses())
        {
            await sessions.SendAsync(status.SessionId, envelope, cancellationToken);
        }
    }

    public static IReadOnlyList<HostEndpoint> Enumerate()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up
                    && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network =>
                {
                    var properties = network.GetIPProperties();

                    // A real LAN adapter has a default gateway; Hyper-V and WSL switches do not,
                    // and a phone can never reach them. Tailscale has no gateway either, so it is
                    // recognised by its address range instead.
                    var routable = properties.GatewayAddresses.Any(gateway =>
                        gateway.Address.AddressFamily == AddressFamily.InterNetwork
                        && !gateway.Address.Equals(IPAddress.Any));
                    return properties.UnicastAddresses
                        .Select(unicast => unicast.Address)
                        .Where(address => routable || IsTailscale(address));
                })
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address)
                    && !IsLinkLocal(address))
                .Distinct()
                .Select(address => IsTailscale(address)
                    ? new HostEndpoint(address.ToString(), HostIdentity.WssPort, KindTailscale, 20)
                    : new HostEndpoint(address.ToString(), HostIdentity.WssPort, KindLan, 10))
                .OrderBy(endpoint => endpoint.Preference)
                .ThenBy(endpoint => endpoint.Host, StringComparer.Ordinal)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<HostEndpoint>();
        }
    }

    public static bool IsTailscale(IPAddress address) => InPrefix(address, TailscalePrefix, TailscalePrefixLength);

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool InPrefix(IPAddress address, IPAddress prefix, int length)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var value = BitConverter.ToUInt32(address.GetAddressBytes().Reverse().ToArray());
        var network = BitConverter.ToUInt32(prefix.GetAddressBytes().Reverse().ToArray());
        var mask = length == 0 ? 0u : uint.MaxValue << (32 - length);
        return (value & mask) == (network & mask);
    }
}
