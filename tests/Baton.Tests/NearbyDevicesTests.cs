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
        Assert.Null(nearby.IsNear("tablet"));
    }
}
