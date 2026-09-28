using System.Web;

namespace Baton.Protocol;

/// <summary>
/// The pairing QR code: <c>baton://pair?h=hostId&amp;n=pcName&amp;f=fingerprint&amp;c=code&amp;e=host:port,host:port</c>.
/// Scanning it pins the certificate before the first connection, so the phone never has to trust
/// whatever answered a UDP broadcast.
/// </summary>
public sealed record PairingLink(
    string HostId,
    string PcName,
    string CertificateFingerprint,
    string Code,
    IReadOnlyList<HostEndpoint> Endpoints)
{
    public const string Scheme = "baton";

    public override string ToString()
    {
        var endpoints = string.Join(",", Endpoints.Select(endpoint => $"{endpoint.Host}:{endpoint.Port}"));
        return $"{Scheme}://pair?h={Uri.EscapeDataString(HostId)}&n={Uri.EscapeDataString(PcName)}"
            + $"&f={Uri.EscapeDataString(CertificateFingerprint)}&c={Uri.EscapeDataString(Code)}"
            + $"&e={Uri.EscapeDataString(endpoints)}";
    }

    public static PairingLink? TryParse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "pair", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        string? hostId = query["h"], name = query["n"], fingerprint = query["f"], code = query["c"];
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var endpoints = (query["e"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item =>
            {
                var separator = item.LastIndexOf(':');
                return separator > 0 && int.TryParse(item[(separator + 1)..], out var port)
                    ? new HostEndpoint(item[..separator], port, "lan", 10)
                    : null;
            })
            .OfType<HostEndpoint>()
            .ToArray();
        return new PairingLink(hostId, name ?? "PC", fingerprint, code, endpoints);
    }
}
