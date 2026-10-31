using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth.Advertisement;

namespace Baton.Host;

/// <summary>
/// Which paired phones are near this PC, from the quiet Bluetooth signal a phone sends when asked
/// to (NearbyBeacon.kt). The signal carries the first 8 bytes of HMAC-SHA256(pairing key,
/// "baton-near:&lt;quarter hour&gt;"), so only this PC recognises a phone, and nobody can follow one
/// by it. Strength is smoothed and the thresholds apart, so a phone at the edge doesn't flicker
/// between near and away. Used only to time suggestions.
/// </summary>
public sealed class NearbyDevices : IDisposable
{
    public const ushort CompanyId = 0xFFFF;
    public const long SlotSeconds = 15 * 60;

    /// <summary>
    /// A phone at the desk. A PC's adapter often sits inside the case, so readings are weak and
    /// jumpy even close by: -68 to -83 dBm for a phone on the desk next to one.
    /// </summary>
    public const double NearRssi = -80;

    /// <summary>A wall or a room away.</summary>
    public const double AwayRssi = -92;

    /// <summary>
    /// A phone not heard from this long has gone (out of range, or Bluetooth off). A room away the
    /// signal usually stops altogether rather than fading; at the desk there's a reading every few seconds.
    /// </summary>
    public static readonly TimeSpan GoneAfter = TimeSpan.FromSeconds(30);

    /// <summary>What Windows reports when it has no strength for a packet: not a reading.</summary>
    private const short NoReading = -127;

    private static readonly byte[] Magic = [(byte)'B', (byte)'N', 1];
    private readonly DeviceRegistry _devices;
    private readonly DiagnosticsLog _diagnostics;
    private readonly ConcurrentDictionary<string, Signal> _signals = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private BluetoothLEAdvertisementWatcher? _watcher;

    public NearbyDevices(DeviceRegistry devices, DiagnosticsLog diagnostics)
    {
        _devices = devices;
        _diagnostics = diagnostics;
        _timer = new Timer(_ => Evaluate(DateTimeOffset.UtcNow), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>A phone came near (true) or went away (false). Any thread.</summary>
    public event Action<string, bool>? Changed;

    /// <summary>Near, away, or null when this PC hasn't heard the phone's signal.</summary>
    public bool? IsNear(string deviceId) => _signals.TryGetValue(deviceId, out var signal) ? signal.Near : null;

    public void Start()
    {
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
            _watcher.Received += (_, args) => OnAdvertisement(args);
            _watcher.Start();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // No Bluetooth on this PC: suggestions keep using idle and lock alone.
            _watcher = null;
            _diagnostics.Record(DiagnosticsCategory.Transport, "nearby.unavailable", ex.Message, severity: DiagnosticsSeverity.Warning);
        }
    }

    private void OnAdvertisement(BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var section in args.Advertisement.ManufacturerData)
        {
            if (section.CompanyId != CompanyId)
            {
                continue;
            }

            var data = section.Data.ToArray();
            if (data.Length == Magic.Length + 8 && data.AsSpan(0, Magic.Length).SequenceEqual(Magic)
                && args.RawSignalStrengthInDBm > NoReading
                && Recognize(data.AsSpan(Magic.Length), DateTimeOffset.UtcNow) is { } deviceId)
            {
                Observe(deviceId, args.RawSignalStrengthInDBm, DateTimeOffset.UtcNow);
            }
        }
    }

    /// <summary>The paired device whose code this is, for this quarter hour or either next to it (clocks differ).</summary>
    private string? Recognize(ReadOnlySpan<byte> token, DateTimeOffset now)
    {
        var slot = now.ToUnixTimeSeconds() / SlotSeconds;
        foreach (var device in _devices.GetDevices())
        {
            for (var offset = -1; offset <= 1; offset++)
            {
                if (CryptographicOperations.FixedTimeEquals(Token(device.TrustKey, slot + offset), token))
                {
                    return device.DeviceId;
                }
            }
        }

        return null;
    }

    /// <summary>A signal heard at <paramref name="rssi"/> dBm.</summary>
    public void Observe(string deviceId, double rssi, DateTimeOffset now)
    {
        var signal = _signals.GetOrAdd(deviceId, _ => new Signal());
        lock (signal)
        {
            // After a silence the old strength says nothing about where the phone is now: one faint
            // reading from the next room must not count as close because the last ones before it were.
            var fresh = signal.Strength is null || now - signal.HeardAt > GoneAfter;
            signal.Strength = fresh ? rssi : signal.Strength * 0.7 + rssi * 0.3;
            signal.HeardAt = now;
        }

        Evaluate(now);
    }

    /// <summary>Applies the thresholds; also called regularly, to notice a phone that fell silent.</summary>
    public void Evaluate(DateTimeOffset now)
    {
        foreach (var (deviceId, signal) in _signals)
        {
            bool? next;
            double strength;
            lock (signal)
            {
                strength = signal.Strength ?? double.NegativeInfinity;
                var gone = now - signal.HeardAt > GoneAfter;
                next = signal.Near switch
                {
                    // The first reading sets where a phone is: being near when Baton starts isn't arriving.
                    null => !gone && strength >= NearRssi,
                    true when gone || strength <= AwayRssi => false,
                    false when !gone && strength >= NearRssi => true,
                    _ => signal.Near
                };
                if (next == signal.Near)
                {
                    continue;
                }

                var first = signal.Near is null;
                signal.Near = next;
                if (first)
                {
                    continue;
                }
            }

            _diagnostics.Record(DiagnosticsCategory.Handoff, next == true ? "nearby.near" : "nearby.away", $"{strength:0} dBm", deviceId);
            Changed?.Invoke(deviceId, next == true);
        }
    }

    /// <summary>"BN", version 1, then this: the first 8 bytes of HMAC-SHA256(key, "baton-near:&lt;slot&gt;").</summary>
    public static byte[] Token(string trustKeyHex, long slot) =>
        HMACSHA256.HashData(Convert.FromHexString(trustKeyHex), Encoding.UTF8.GetBytes($"baton-near:{slot}"))[..8];

    public void Dispose()
    {
        _timer.Dispose();
        _watcher?.Stop();
    }

    private sealed class Signal
    {
        public double? Strength;
        public DateTimeOffset HeardAt;
        public bool? Near;
    }
}
