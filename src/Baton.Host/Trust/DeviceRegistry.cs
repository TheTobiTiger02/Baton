using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Protocol;

namespace Baton.Host;

public sealed record PairingTicket(string Code, string TrustKey, DateTimeOffset ExpiresAt);

public sealed record PairingResult(bool Accepted, string DeviceId, string? TrustKey, string? Reason, string? ErrorCode = null);

public sealed record LinkedDevice(
    string DeviceId,
    string DisplayName,
    string Model,
    string TrustKey,
    DateTimeOffset PairedAt,
    DateTimeOffset LastSeenAt,
    bool Connected);

/// <summary>
/// Trusted phones. Persisted as two checksummed snapshots with DPAPI-protected trust keys and a
/// monotonically increasing revision; a forgotten phone leaves a tombstone so it cannot return from
/// an older backup.
/// </summary>
public sealed class DeviceRegistry
{
    private const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Age after which an atomic-write temp file is certainly abandoned. A live write finishes in
    /// milliseconds; anything this old belongs to a process that died mid-write.
    /// </summary>
    internal static readonly TimeSpan StaleTempFileAge = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, LinkedDevice> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _deviceRevisions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RevocationTombstone> _revocations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PairingTicket> _tickets = new(StringComparer.Ordinal);
    private readonly object _persistenceGate = new();
    private readonly string? _storagePath;
    private readonly string? _backupPath;
    private long _revision;

    public DeviceRegistry(string? storagePath)
    {
        _storagePath = string.IsNullOrWhiteSpace(storagePath) ? null : storagePath;
        _backupPath = _storagePath is null ? null : $"{_storagePath}.bak";
        RemoveStaleTempFiles(_storagePath);
        RemoveStaleTempFiles(_backupPath);
        LoadPersistedDevices();
    }

    /// <summary>Raised after any device is added, updated, connected, disconnected or forgotten.</summary>
    public event Action? Changed;

    public string? LastPersistenceError { get; private set; }

    public PairingTicket OpenPairingTicket(TimeSpan? ttl = null)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var trustKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var ticket = new PairingTicket(code, trustKey, DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(5)));
        _tickets[code] = ticket;
        return ticket;
    }

    public PairingResult ConfirmPairing(PairHelloPayload hello)
    {
        if (!_tickets.TryRemove(hello.PairingCode, out var ticket))
        {
            return new PairingResult(false, hello.DeviceId, null,
                "The pairing code was not recognized. Show a new code on the PC.", "pairing_code_rejected");
        }

        if (ticket.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new PairingResult(false, hello.DeviceId, null,
                "The pairing code expired. Show a new code on the PC.", "pairing_code_expired");
        }

        var deviceId = string.IsNullOrWhiteSpace(hello.DeviceId) ? Guid.NewGuid().ToString("N") : hello.DeviceId;
        lock (_persistenceGate)
        {
            var now = DateTimeOffset.UtcNow;

            // A new pairing is explicit user consent, so it is the only operation that may
            // supersede a previous Forget tombstone.
            _revocations.TryRemove(deviceId, out _);
            _devices[deviceId] = new LinkedDevice(deviceId, hello.DisplayName, hello.Model, ticket.TrustKey, now, now, Connected: true);
            _deviceRevisions[deviceId] = _revision + 1;
            SaveLocked();
        }

        Changed?.Invoke();
        return new PairingResult(true, deviceId, ticket.TrustKey, null);
    }

    public bool AuthenticateProof(string deviceId, string challengeId, string nonce, string hostId, string proof)
    {
        if (!_devices.TryGetValue(deviceId, out var device) || string.IsNullOrWhiteSpace(proof))
        {
            return false;
        }

        var expected = ComputeChallengeProof(device.TrustKey, challengeId, nonce, deviceId, hostId);
        return expected.Length == proof.Length
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(proof.ToLowerInvariant()));
    }

    public static string ComputeChallengeProof(string trustKey, string challengeId, string nonce, string deviceId, string hostId)
    {
        using var hmac = new HMACSHA256(Convert.FromHexString(trustKey));
        var message = Encoding.UTF8.GetBytes($"{challengeId}:{nonce}:{deviceId}:{hostId}");
        return Convert.ToHexString(hmac.ComputeHash(message)).ToLowerInvariant();
    }

    public IReadOnlyList<LinkedDevice> GetDevices() =>
        _devices.Values.OrderBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();

    public LinkedDevice? GetDevice(string deviceId) =>
        _devices.TryGetValue(deviceId, out var device) ? device : null;

    public void MarkConnected(string deviceId, bool connected)
    {
        if (!_devices.TryGetValue(deviceId, out var existing))
        {
            return;
        }

        _devices[deviceId] = existing with { Connected = connected, LastSeenAt = DateTimeOffset.UtcNow };
        Changed?.Invoke();
    }

    public void UpdateInfo(string deviceId, DeviceInfoPayload info)
    {
        if (!_devices.TryGetValue(deviceId, out var existing))
        {
            return;
        }

        var updated = existing with
        {
            DisplayName = string.IsNullOrWhiteSpace(info.DisplayName) ? existing.DisplayName : info.DisplayName,
            Model = string.IsNullOrWhiteSpace(info.Model) ? existing.Model : info.Model,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        if (updated.DisplayName == existing.DisplayName && updated.Model == existing.Model)
        {
            return;
        }

        lock (_persistenceGate)
        {
            _devices[deviceId] = updated;
            _deviceRevisions[deviceId] = _revision + 1;
            SaveLocked();
        }

        Changed?.Invoke();
    }

    public bool Revoke(string deviceId)
    {
        bool removed;
        lock (_persistenceGate)
        {
            removed = _devices.TryRemove(deviceId, out _);
            _deviceRevisions.TryRemove(deviceId, out _);
            _revocations[deviceId] = new RevocationTombstone(deviceId, DateTimeOffset.UtcNow, _revision + 1);
            SaveLocked();
        }

        Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// Deletes leftover <c>{path}.{guid}.tmp</c> files. They are never resumable - the real file is
    /// only ever replaced by a completed temp - so the only question is whether one might still be
    /// in use, which the age threshold answers.
    /// </summary>
    internal static int RemoveStaleTempFiles(string? path, DateTime? nowUtc = null)
    {
        var directory = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = (nowUtc ?? DateTime.UtcNow) - StaleTempFileAge;
        var removed = 0;
        foreach (var candidate in Directory.EnumerateFiles(directory, $"{Path.GetFileName(path)}.*.tmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(candidate) < cutoff)
                {
                    File.Delete(candidate);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort; a locked orphan is retried on the next start.
            }
        }

        return removed;
    }

    private void LoadPersistedDevices()
    {
        if (_storagePath is null || _backupPath is null)
        {
            return;
        }

        lock (_persistenceGate)
        {
            var snapshots = new List<(string Path, TrustStoreDocument Document)>();
            var errors = new List<string>();
            foreach (var path in new[] { _storagePath, _backupPath })
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    snapshots.Add((path, ReadSnapshot(path)));
                }
                catch (Exception ex) when (IsPersistenceException(ex))
                {
                    errors.Add($"{Path.GetFileName(path)} could not be loaded: {ex.Message}");
                }
            }

            LastPersistenceError = errors.Count == 0 ? null : string.Join(" ", errors);
            if (snapshots.Count == 0)
            {
                return;
            }

            var selected = snapshots
                .OrderByDescending(snapshot => snapshot.Document.Revision)
                .ThenBy(snapshot => snapshot.Path == _storagePath ? 0 : 1)
                .First();
            var revocations = snapshots
                .SelectMany(snapshot => snapshot.Document.Revocations)
                .GroupBy(item => item.DeviceId, StringComparer.Ordinal)
                .Select(group => group.MaxBy(item => item.Revision)!);
            foreach (var revocation in revocations)
            {
                _revocations[revocation.DeviceId] = revocation;
            }

            foreach (var persisted in selected.Document.Devices)
            {
                if (_revocations.TryGetValue(persisted.DeviceId, out var tombstone) && tombstone.Revision >= persisted.Revision)
                {
                    continue;
                }

                _devices[persisted.DeviceId] = new LinkedDevice(
                    persisted.DeviceId,
                    persisted.DisplayName,
                    persisted.Model,
                    SecretProtector.Unprotect(persisted.ProtectedTrustKey),
                    persisted.PairedAt,
                    persisted.LastSeenAt,
                    Connected: false);
                _deviceRevisions[persisted.DeviceId] = persisted.Revision;
            }

            _revision = snapshots.Max(snapshot => snapshot.Document.Revision);

            // Repair a missing or corrupt peer snapshot immediately.
            if (errors.Count > 0 || snapshots.Count == 1)
            {
                SaveLocked();
            }
        }
    }

    private void SaveLocked()
    {
        if (_storagePath is null || _backupPath is null)
        {
            return;
        }

        try
        {
            var revision = _revision + 1;
            var devices = _devices.Values
                .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
                .Select(device => new PersistedDevice(
                    device.DeviceId,
                    device.DisplayName,
                    device.Model,
                    SecretProtector.Protect(device.TrustKey),
                    device.PairedAt,
                    device.LastSeenAt,
                    Math.Min(_deviceRevisions.GetValueOrDefault(device.DeviceId, revision), revision)))
                .ToArray();
            var revocations = _revocations.Values.OrderBy(item => item.DeviceId, StringComparer.Ordinal).ToArray();
            var content = new TrustStoreContent(CurrentSchemaVersion, revision, devices, revocations);
            var document = new TrustStoreDocument(content.SchemaVersion, revision, ComputeChecksum(content), devices, revocations);
            var json = JsonSerializer.Serialize(document, JsonOptions);

            // Write the redundant snapshot first. If the process is interrupted, startup still
            // selects the valid document with the greatest revision.
            AtomicWrite(_backupPath, json);
            AtomicWrite(_storagePath, json);
            _revision = revision;
            LastPersistenceError = null;
        }
        catch (Exception ex) when (IsPersistenceException(ex))
        {
            LastPersistenceError = $"Trust store save failed: {ex.Message}";
        }
    }

    private static TrustStoreDocument ReadSnapshot(string path)
    {
        var document = JsonSerializer.Deserialize<TrustStoreDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new JsonException("Trust store document was missing.");
        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new JsonException($"Unsupported trust store schema {document.SchemaVersion}.");
        }

        var expected = ComputeChecksum(new TrustStoreContent(document.SchemaVersion, document.Revision, document.Devices, document.Revocations));
        if (!string.Equals(expected, document.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("Trust store checksum did not match its contents.");
        }

        // Validate every protected value while selecting a snapshot, so a damaged DPAPI blob
        // falls back to the other snapshot.
        foreach (var device in document.Devices)
        {
            _ = SecretProtector.Unprotect(device.ProtectedTrustKey);
        }

        return document;
    }

    private static string ComputeChecksum(TrustStoreContent content) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, CanonicalJsonOptions))).ToLowerInvariant();

    private static void AtomicWrite(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(contents));
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

    private static bool IsPersistenceException(Exception ex) =>
        ex is IOException or JsonException or UnauthorizedAccessException or CryptographicException
            or FormatException or Win32Exception or PlatformNotSupportedException;

    private sealed record TrustStoreDocument(
        int SchemaVersion,
        long Revision,
        string Checksum,
        IReadOnlyList<PersistedDevice> Devices,
        IReadOnlyList<RevocationTombstone> Revocations);

    private sealed record TrustStoreContent(
        int SchemaVersion,
        long Revision,
        IReadOnlyList<PersistedDevice> Devices,
        IReadOnlyList<RevocationTombstone> Revocations);

    private sealed record PersistedDevice(
        string DeviceId,
        string DisplayName,
        string Model,
        string ProtectedTrustKey,
        DateTimeOffset PairedAt,
        DateTimeOffset LastSeenAt,
        long Revision);

    private sealed record RevocationTombstone(string DeviceId, DateTimeOffset RevokedAt, long Revision);
}
