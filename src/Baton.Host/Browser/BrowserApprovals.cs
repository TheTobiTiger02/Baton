using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Baton.Host.Browser;

/// <summary>A browser the user allowed to connect, known by the hash of the token it was given.</summary>
public sealed record ApprovedBrowser(string Id, string Browser, string TokenHash, DateTimeOffset ApprovedAt);

/// <summary>
/// Browsers the user allowed, once, to show their tabs in Baton. Store builds of the extension are
/// the same file on every PC, so they can't carry a secret; instead the first connection asks, and
/// an allowed browser gets its own token to present from then on. Only hashes are kept here.
/// </summary>
public sealed class BrowserApprovals(string? path)
{
    private readonly object _gate = new();
    private List<ApprovedBrowser>? _approved;

    public event Action? Changed;

    public IReadOnlyList<ApprovedBrowser> Approved
    {
        get
        {
            lock (_gate)
            {
                return [.. Load()];
            }
        }
    }

    public bool IsApproved(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var hash = Hash(token);
        lock (_gate)
        {
            return Load().Any(browser => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(browser.TokenHash), Encoding.ASCII.GetBytes(hash)));
        }
    }

    /// <summary>Allows a browser and returns the token it will present from now on.</summary>
    public string Approve(string browserName)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        lock (_gate)
        {
            Load().Add(new ApprovedBrowser(Guid.NewGuid().ToString("n"), browserName, Hash(token), DateTimeOffset.UtcNow));
            Save();
        }

        Changed?.Invoke();
        return token;
    }

    public void Revoke(string id)
    {
        lock (_gate)
        {
            Load().RemoveAll(browser => browser.Id == id);
            Save();
        }

        Changed?.Invoke();
    }

    private List<ApprovedBrowser> Load()
    {
        if (_approved is not null)
        {
            return _approved;
        }

        try
        {
            _approved = path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<List<ApprovedBrowser>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Browsers ask again; nothing else depends on this file.
            _approved = [];
        }

        return _approved;
    }

    private void Save()
    {
        if (path is null)
        {
            return;
        }

        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_approved));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this session only.
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
