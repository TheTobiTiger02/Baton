using System.Collections.Concurrent;

namespace Baton.Host;

/// <summary>A phone's battery as it last reported it.</summary>
public sealed record DeviceBattery(int Level, bool Charging);

/// <summary>
/// Each connected phone's battery. Kept in memory only: it is true while the phone is connected,
/// and stale the moment it isn't.
/// </summary>
public sealed class DeviceBatteries
{
    private readonly ConcurrentDictionary<string, DeviceBattery> _batteries = new(StringComparer.Ordinal);

    public event Action? Changed;

    public DeviceBattery? Get(string deviceId) => _batteries.TryGetValue(deviceId, out var battery) ? battery : null;

    public void Set(string deviceId, int? level, bool? charging)
    {
        if (level is not (>= 0 and <= 100))
        {
            return;
        }

        var battery = new DeviceBattery(level.Value, charging == true);
        if (_batteries.TryGetValue(deviceId, out var current) && current == battery)
        {
            return;
        }

        _batteries[deviceId] = battery;
        Changed?.Invoke();
    }

    public void Clear(string deviceId)
    {
        if (_batteries.TryRemove(deviceId, out _))
        {
            Changed?.Invoke();
        }
    }
}
