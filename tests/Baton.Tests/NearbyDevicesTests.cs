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
    public void WalkingOffWhileNobodyUsesThePcIsLeavingAtOnce()
    {
        var idle = TimeSpan.FromMinutes(1);
        using var nearby = new NearbyDevices(new DeviceRegistry(null), new DiagnosticsLog(), () => idle);
        var changes = new List<bool>();
        nearby.Changed += (_, near) => changes.Add(near);
        var now = DateTimeOffset.UtcNow;
        for (var second = 0; second < 6; second++) nearby.Observe("phone", -66, now.AddSeconds(second));

        // Typing at the PC while the phone counts steps (pacing, a colleague's phone in a pocket): still here.
        idle = TimeSpan.FromSeconds(1);
        nearby.Walking("phone", now.AddSeconds(6));
        Assert.Empty(changes);

        // Up from the desk: away at once, though the first steps still read as the desk.
        idle = TimeSpan.FromSeconds(10);
        nearby.Walking("phone", now.AddSeconds(8));
        Assert.Equal([false], changes);
        nearby.Observe("phone", -68, now.AddSeconds(9));
        nearby.Observe("phone", -66, now.AddSeconds(10));
        Assert.Equal([false], changes);

        // Out of range and back: near again.
        nearby.Evaluate(now.AddSeconds(40));
        idle = TimeSpan.FromSeconds(60);
        for (var second = 60; second < 70; second++) nearby.Observe("phone", -66, now.AddSeconds(second));
        Assert.Equal([false, true], changes);

        // Walked off but stayed in the room, then sat down and used the PC again: near again.
        idle = TimeSpan.FromSeconds(10);
        nearby.Walking("phone", now.AddSeconds(70));
        nearby.Observe("phone", -66, now.AddSeconds(75));
        Assert.Equal([false, true, false], changes);
        idle = TimeSpan.FromSeconds(1);
        nearby.Observe("phone", -66, now.AddSeconds(90));
        Assert.Equal([false, true, false, true], changes);
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
