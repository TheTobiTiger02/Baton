using Baton.Host;

namespace Baton.Tests;

public class WakeRelaysTests
{
    [Fact]
    public void KeepsWhatDevicesOfferAcrossRestarts()
    {
        var path = Path.Combine(Path.GetTempPath(), "baton-relays-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var relays = new WakeRelays(path);
            var changes = 0;
            relays.Changed += () => changes++;

            relays.Set("tablet", ["100.101.1.2", "192.168.178.40", "not an address"]);
            relays.Set("tablet", ["100.101.1.2", "192.168.178.40"]);
            relays.Set("phone", null);
            Assert.Equal(1, changes);
            Assert.Equal(["100.101.1.2", "192.168.178.40"], relays.All["tablet"]);

            // The PC restarts while the tablet is offline: phones still learn about it.
            Assert.Equal(["100.101.1.2", "192.168.178.40"], new WakeRelays(path).All["tablet"]);

            relays.Set("tablet", []);
            Assert.Equal(2, changes);
            Assert.Empty(new WakeRelays(path).All);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
