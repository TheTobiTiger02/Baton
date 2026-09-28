using System.Net;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Baton.Protocol;

namespace Baton.Host;

public sealed class HostIdentity : IDisposable
{
    private static readonly object PersistenceGate = new();
    public const int BrowserPort = 7836;
    public const int DiscoveryPort = 7837;
    public const int WssPort = 7838;
    public const int StreamPort = 7839;

    private HostIdentity(
        string hostId,
        DateTimeOffset createdAt,
        X509Certificate2 certificate,
        string storageDirectory)
    {
        HostId = hostId;
        CreatedAt = createdAt;
        Certificate = certificate;
        StorageDirectory = storageDirectory;
        CertificateFingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
    }

    public string HostId { get; }
    public string PcName => Environment.MachineName;
    public DateTimeOffset CreatedAt { get; }
    public X509Certificate2 Certificate { get; }
    public string CertificateFingerprint { get; }
    public string StorageDirectory { get; }

    public static string DefaultStorageDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Baton");

    public static HostIdentity LoadOrCreate(string? storageDirectory = null)
    {
        lock (PersistenceGate)
        {
            return LoadOrCreateLocked(storageDirectory);
        }
    }

    private static HostIdentity LoadOrCreateLocked(string? storageDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(storageDirectory)
            ? DefaultStorageDirectory
            : storageDirectory;
        Directory.CreateDirectory(directory);

        var identityPath = Path.Combine(directory, "host-identity.json");
        var identityBackupPath = $"{identityPath}.bak";
        var certificatePath = Path.Combine(directory, "host-certificate.pfx");
        var certificateBackupPath = $"{certificatePath}.bak";

        var identity = ReadIdentity(identityPath)
            ?? ReadIdentity(identityBackupPath)
            ?? new PersistedHostIdentity(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                SecretProtector.Protect(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        if (string.IsNullOrWhiteSpace(identity.ProtectedCertificatePassword))
        {
            identity = identity with
            {
                ProtectedCertificatePassword = SecretProtector.Protect(
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
            };
        }

        var certificatePassword = SecretProtector.Unprotect(identity.ProtectedCertificatePassword);

        var certificate = ReadCertificate(certificatePath, certificatePassword)
            ?? ReadCertificate(certificateBackupPath, certificatePassword)
            ?? CreateCertificate(identity.HostId);

        var identityJson = JsonSerializer.Serialize(identity, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
        var certificateBytes = certificate.Export(X509ContentType.Pfx, certificatePassword);

        AtomicWrite(identityBackupPath, System.Text.Encoding.UTF8.GetBytes(identityJson));
        AtomicWrite(identityPath, System.Text.Encoding.UTF8.GetBytes(identityJson));
        AtomicWrite(certificateBackupPath, certificateBytes);
        AtomicWrite(certificatePath, certificateBytes);

        return new HostIdentity(identity.HostId, identity.CreatedAt, certificate, directory);
    }

    public void Dispose() => Certificate.Dispose();

    private static PersistedHostIdentity? ReadIdentity(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var identity = JsonSerializer.Deserialize<PersistedHostIdentity>(
                File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return identity is not null && Guid.TryParseExact(identity.HostId, "N", out _)
                ? identity
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or Win32Exception or FormatException)
        {
            return null;
        }
    }

    private static X509Certificate2? ReadCertificate(string path, string password)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path,
                password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            if (!certificate.HasPrivateKey || certificate.NotAfter <= DateTime.UtcNow.AddDays(30))
            {
                certificate.Dispose();
                return null;
            }

            return certificate;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static X509Certificate2 CreateCertificate(string hostId)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN=Baton {hostId}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            true));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var address in GetLanAddresses())
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(30));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private static IEnumerable<IPAddress> GetLanAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Where(address => !IPAddress.IsLoopback(address))
                .Distinct()
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed record PersistedHostIdentity(
        string HostId,
        DateTimeOffset CreatedAt,
        string? ProtectedCertificatePassword = null);
}
