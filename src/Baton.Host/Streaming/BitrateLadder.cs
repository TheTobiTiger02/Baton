namespace Baton.Host.Streaming;

/// <summary>How much a stream may use: adapt to the link, always the best, or little.</summary>
public enum StreamQuality { Auto, High, DataSaver }

/// <summary>
/// The bitrate of one stream, stepped down the moment the link can't keep up (the phone fell
/// behind and video was dropped, or data waits in queues) and back up slowly once it has kept up
/// for a while, so a passing hiccup costs a few blurry seconds instead of a stutter that lasts.
/// </summary>
public sealed class BitrateLadder
{
    /// <summary>The last step is for a weak mobile connection away from home.</summary>
    public static readonly int[] Steps = [12_000_000, 6_000_000, 3_000_000, 1_500_000, 750_000];

    /// <summary>Waiting this much longer than the link itself takes means it is filling up.</summary>
    public static readonly TimeSpan QueueingLimit = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan DownSpacing = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UpAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BaselineWindow = TimeSpan.FromSeconds(30);
    private readonly Queue<(DateTimeOffset At, TimeSpan Time)> _roundTrips = new();

    private readonly int _best;
    private readonly int _worst;
    private int _step;
    private DateTimeOffset _changedAt;
    private DateTimeOffset _droppedAt;

    /// <param name="remote">The link goes through Tailscale or the internet, not the home network: start lower.</param>
    public BitrateLadder(StreamQuality quality, bool remote, DateTimeOffset now)
    {
        (_best, _worst) = quality switch
        {
            StreamQuality.High => (0, 0),
            StreamQuality.DataSaver => (2, Steps.Length - 1),
            _ => (0, Steps.Length - 1)
        };
        _step = Math.Clamp(remote ? 1 : 0, _best, _worst);
        _changedAt = _droppedAt = now;
    }

    public int Bitrate => Steps[_step];

    /// <summary>Below this the picture is sent at 720p: fewer, sharper pixels beat many blurry ones.</summary>
    public const int ReducedBelow = 3_000_000;

    public bool Reduced => Bitrate < ReducedBelow;

    /// <summary>A frame size scaled down to 1280 on its long edge (unchanged when already smaller).</summary>
    public static (int Width, int Height) Reduce((int Width, int Height) size)
    {
        var scale = Math.Min(1.0, 1280.0 / Math.Max(size.Width, size.Height));
        return (Math.Max(64, (int)(size.Width * scale) & ~1), Math.Max(64, (int)(size.Height * scale) & ~1));
    }

    /// <summary>Video was dropped. Returns the new bitrate when it went down.</summary>
    public int? Dropped(DateTimeOffset now)
    {
        _droppedAt = now;
        if (_step >= _worst || now - _changedAt < DownSpacing)
        {
            return null;
        }

        _step++;
        _changedAt = now;
        return Bitrate;
    }

    /// <summary>
    /// A probe came back after <paramref name="roundTrip"/>. True when it waited too long in
    /// queues: longer than the fastest round trip of the last half minute, which is what the link
    /// itself takes (a few milliseconds at home, often over a hundred on mobile data), by more than
    /// <see cref="QueueingLimit"/>.
    /// </summary>
    public bool Congested(TimeSpan roundTrip, DateTimeOffset now)
    {
        while (_roundTrips.TryPeek(out var oldest) && now - oldest.At > BaselineWindow)
        {
            _roundTrips.Dequeue();
        }

        _roundTrips.Enqueue((now, roundTrip));
        return roundTrip - _roundTrips.Min(sample => sample.Time) > QueueingLimit;
    }

    /// <summary>Called regularly. Returns the new bitrate when the link earned a step up.</summary>
    public int? Tick(DateTimeOffset now)
    {
        if (_step <= _best || now - _droppedAt < UpAfter || now - _changedAt < UpAfter)
        {
            return null;
        }

        _step--;
        _changedAt = now;
        return Bitrate;
    }

    /// <summary>Tailscale addresses (100.64.0.0/10): the phone is away from home.</summary>
    public static bool IsRemote(System.Net.IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 100 && (bytes[1] & 0xC0) == 64;
    }
}
