using System.Collections.Concurrent;
using System.Text.Json;

namespace Baton.Host;

/// <summary>
/// Paired devices that offered to wake this PC for the others, with the addresses they take
/// requests on. Kept on disk: a phone away from home needs the list while this PC is asleep, and
/// a phone that connects right after a restart must not be told there are none.
/// </summary>
public sealed class WakeRelays
{
    private readonly string? _path;
    private readonly ConcurrentDictionary<string, string[]> _relays = new(StringComparer.Ordinal);

    public WakeRelays(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var (deviceId, addresses) in JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path)) ?? [])
            {
                _relays[deviceId] = addresses;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The devices report again when they next connect.
        }
    }

    /// <summary>A device started or stopped offering, or its addresses changed. Any thread.</summary>
    public event Action? Changed;

    /// <summary>Each relay's device and addresses.</summary>
    public IReadOnlyDictionary<string, string[]> All => _relays;

    /// <summary>What a device reported: its addresses, or null when it doesn't offer (any more).</summary>
    public void Set(string deviceId, IReadOnlyList<string>? addresses)
    {
        var next = addresses?.Where(address => System.Net.IPAddress.TryParse(address, out _)).Distinct().Take(8).ToArray();
        if (next is { Length: > 0 })
        {
            if (_relays.TryGetValue(deviceId, out var current) && current.SequenceEqual(next))
            {
                return;
            }

            _relays[deviceId] = next;
        }
        else if (!_relays.TryRemove(deviceId, out _))
        {
            return;
        }

        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_relays.ToDictionary(pair => pair.Key, pair => pair.Value)));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the device reports again on its next connection.
        }
    }
}
