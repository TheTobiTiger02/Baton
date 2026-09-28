using System.Collections.Concurrent;
using System.Text.Json;

namespace Baton.Host;

/// <summary>
/// Names the user gave phones on this PC ("Tobias's S21" → "S21"), kept apart from the pairing
/// store so renaming can never touch pairings. A phone without one keeps the name it reports.
/// </summary>
public sealed class DeviceNames
{
    private readonly string? _path;
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);

    public DeviceNames(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var (deviceId, name) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [])
            {
                _names[deviceId] = name;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Names are a convenience: a damaged file means phones show their own names again.
        }
    }

    /// <summary>A name was set or cleared. Any thread.</summary>
    public event Action? Changed;

    public string? Get(string deviceId) => _names.GetValueOrDefault(deviceId);

    /// <summary>Sets the name shown for a phone; null or blank goes back to the phone's own name.</summary>
    public void Set(string deviceId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _names.TryRemove(deviceId, out _);
        }
        else
        {
            _names[deviceId] = name.Trim();
        }

        if (_path is not null)
        {
            try
            {
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_names.ToDictionary(pair => pair.Key, pair => pair.Value)));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept for this session only.
            }
        }

        Changed?.Invoke();
    }
}
