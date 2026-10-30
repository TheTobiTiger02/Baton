using System.Net.WebSockets;
using Baton.Host;

namespace Baton.Tests;

public class SessionHubTests
{
    [Fact]
    public async Task StaleSessionsAreClosedByDeviceAndForgotten()
    {
        using var hub = new WebSocketSessionHub();
        using var quiet = WebSocket.CreateFromStream(new MemoryStream(), isServer: true, subProtocol: null, TimeSpan.Zero);
        using var lively = WebSocket.CreateFromStream(new MemoryStream(), isServer: true, subProtocol: null, TimeSpan.Zero);
        var quietGeneration = hub.Attach("quiet-phone", quiet);
        hub.Attach("lively-phone", lively);
        hub.MarkActivity("quiet-phone", quiet, quietGeneration, DateTimeOffset.UtcNow.AddMinutes(-5));

        var closed = await hub.CloseStaleAsync(DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        Assert.Equal(["quiet-phone"], closed);
        // The session handler relies on this to report the phone as gone: its own Detach finds
        // nothing left to remove.
        Assert.False(hub.HasSession("quiet-phone"));
        Assert.False(hub.Detach("quiet-phone", quiet));
        Assert.True(hub.HasSession("lively-phone"));
    }

    [Fact]
    public void ReconnectReasonsCountEachEnd()
    {
        var log = new DiagnosticsLog();
        log.MarkDisconnected("network error");
        log.MarkDisconnected("closed by phone (NormalClosure)");
        log.MarkDisconnected("network error");

        Assert.Equal(
            [new KeyValuePair<string, int>("network error", 2), new KeyValuePair<string, int>("closed by phone (NormalClosure)", 1)],
            log.ReconnectReasons());
    }
}

public class DeviceBatteriesTests
{
    [Fact]
    public void ReportsOnlyChangesAndForgetsDisconnectedPhones()
    {
        var batteries = new DeviceBatteries();
        var changes = 0;
        batteries.Changed += () => changes++;

        batteries.Set("phone", 80, false);
        batteries.Set("phone", 80, false);
        batteries.Set("phone", 80, true);
        batteries.Set("phone", null, true);
        batteries.Set("phone", 140, true);

        Assert.Equal(2, changes);
        Assert.Equal(new DeviceBattery(80, true), batteries.Get("phone"));

        batteries.Clear("phone");
        batteries.Clear("phone");
        Assert.Equal(3, changes);
        Assert.Null(batteries.Get("phone"));
    }
}

public class FileTransfersTests
{
    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("../../Windows/evil.exe", "evil.exe")]
    [InlineData(@"C:\Users\x\notes.txt", "notes.txt")]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("...", "file")]
    public void SafeNameKeepsOnlyTheFileName(string offered, string saved) =>
        Assert.Equal(saved, Baton.Host.Media.FileTransfers.SafeName(offered));

    [Fact]
    public void UniquePathNumbersRepeats()
    {
        var folder = Directory.CreateTempSubdirectory("baton-files").FullName;
        try
        {
            Assert.Equal(Path.Combine(folder, "photo.jpg"), Baton.Host.Media.FileTransfers.UniquePath(folder, "photo.jpg"));
            File.WriteAllText(Path.Combine(folder, "photo.jpg"), "");
            File.WriteAllText(Path.Combine(folder, "photo (2).jpg"), "");
            Assert.Equal(Path.Combine(folder, "photo (3).jpg"), Baton.Host.Media.FileTransfers.UniquePath(folder, "photo.jpg"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class WakeOnLanTests
{
    [Theory]
    [InlineData("192.168.178.125", "255.255.255.0", "192.168.178.255")]
    [InlineData("10.1.2.3", "255.255.0.0", "10.1.255.255")]
    public void BroadcastAddressCoversTheWholeNetwork(string address, string mask, string broadcast) =>
        Assert.Equal(broadcast, Baton.Host.EndpointEnumerator.Broadcast(System.Net.IPAddress.Parse(address), System.Net.IPAddress.Parse(mask)).ToString());
}

public class KeyComboTests
{
    [Fact]
    public void ReadsCombinationsAsPeopleWriteThem()
    {
        Assert.Equal(((ushort)0x54, new ushort[] { 0x11, 0x10 }), Normalize(Baton.Media.KeyCombo.Parse("Ctrl+Shift+T")));
        Assert.Equal(((ushort)0x44, new ushort[] { 0x5B }), Normalize(Baton.Media.KeyCombo.Parse("win + d")));
        Assert.Equal(((ushort)0x09, new ushort[] { 0x5B }), Normalize(Baton.Media.KeyCombo.Parse("Win+Tab")));
        Assert.Equal(((ushort)0x73, new ushort[] { 0x12 }), Normalize(Baton.Media.KeyCombo.Parse("Alt+F4")));
        Assert.Null(Baton.Media.KeyCombo.Parse("Ctrl+Banana"));
        Assert.Null(Baton.Media.KeyCombo.Parse("Hyper+T"));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://192.168.1.2:8080/x", true)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("notepad.exe", false)]
    public void ShortcutsOpenOnlyWebPages(string address, bool opens) =>
        Assert.Equal(opens, Baton.Host.Streaming.PcRemote.IsWebAddress(address));

    private static (ushort, ushort[])? Normalize((ushort Key, IReadOnlyList<ushort> Modifiers)? combo) =>
        combo is { } value ? (value.Key, value.Modifiers.ToArray()) : null;
}
