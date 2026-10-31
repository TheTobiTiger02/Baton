using Baton.Host;

namespace Baton.Tests;

public class NearbyDevicesTests
{
    private const string Key = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    [Fact]
    public void TheCodeMatchesThePhones()
    {
        // The same vector as NearbyBeaconTest.kt.
        Assert.Equal("7409163e5d578140", Convert.ToHexString(NearbyDevices.Token(Key, 1234)).ToLowerInvariant());
    }

    [Fact]
    public void TheDesksDipsAreNotLeaving()
    {
        using var nearby = new NearbyDevices(new DeviceRegistry(null), new DiagnosticsLog());
        var now = DateTimeOffset.UtcNow;
        // The first walk test's desk readings, a reading about every 2 s, dips to -89 among them.
        var desk = new[] { -70, -67, -73, -62, -68, -66, -73, -62, -89, -77, -84, -76, -83, -69, -71, -67, -70, -68, -62, -64, -67, -73, -64 };
        for (var index = 0; index < desk.Length; index++)
        {
            nearby.Observe("phone", desk[index], now.AddSeconds(index * 2));
            Assert.True(nearby.IsNear("phone"));
        }
    }

    [Fact]
    public void NearAndAwayNeedAClearChangeAndStartingNearIsNotArriving()
    {
        var registry = new DeviceRegistry(null);
        using var nearby = new NearbyDevices(registry, new DiagnosticsLog());
        var changes = new List<(string, bool)>();
        nearby.Changed += (device, near) => changes.Add((device, near));
        var now = DateTimeOffset.UtcNow;

        // Already at the desk when Baton starts.
        nearby.Observe("phone", -60, now);
        Assert.True(nearby.IsNear("phone"));
        Assert.Empty(changes);

        // A dip or two stays near; walking off goes away.
        nearby.Observe("phone", -80, now.AddSeconds(1));
        Assert.Empty(changes);
        for (var second = 2; second < 12; second++) nearby.Observe("phone", -95, now.AddSeconds(second));
        Assert.Equal([("phone", false)], changes);

        // Back at the desk.
        for (var second = 20; second < 30; second++) nearby.Observe("phone", -60, now.AddSeconds(second));
        Assert.Equal([("phone", false), ("phone", true)], changes);

        // Silence is going away too: out of range, or Bluetooth off.
        var gone = now.AddSeconds(29) + NearbyDevices.GoneAfter + TimeSpan.FromSeconds(1);
        nearby.Evaluate(gone);
        Assert.Equal(("phone", false), changes[^1]);

        // A faint reading from the next room after the silence isn't coming back, whatever came before.
        nearby.Observe("phone", -84, gone.AddSeconds(5));
        Assert.False(nearby.IsNear("phone"));

        // Still heard, but weakly, in the hallway: away once the smoothed signal has stayed weak for 6 s.
        var back = gone.AddMinutes(1);
        for (var second = 0; second < 10; second++) nearby.Observe("phone", -65, back.AddSeconds(second));
        Assert.True(nearby.IsNear("phone"));
        for (var second = 10; second < 40; second += 2)
        {
            nearby.Observe("phone", second % 4 == 0 ? -84 : -86, back.AddSeconds(second));
        }

        Assert.False(nearby.IsNear("phone"));

        // A signal that fades and stops: away after the shorter silence.
        var again = back.AddMinutes(1);
        for (var second = 0; second < 10; second++) nearby.Observe("phone", -65, again.AddSeconds(second));
        // As on the walk tests: the last readings in the 80s, smoothed to about -83 when it stops.
        foreach (var (rssi, second) in new[] { (-84, 10), (-88, 11), (-90, 12), (-90, 13) }) nearby.Observe("phone", rssi, again.AddSeconds(second));
        nearby.Evaluate(again.AddSeconds(13 + 4));
        Assert.True(nearby.IsNear("phone"));
        nearby.Evaluate(again.AddSeconds(13) + NearbyDevices.FadedAfter + TimeSpan.FromSeconds(1));
        Assert.False(nearby.IsNear("phone"));
        Assert.Null(nearby.IsNear("tablet"));
    }
}
