using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Baton.Host;
using Baton.Host.Browser;
using Baton.Host.Handoff;
using Baton.Host.Media;
using Baton.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Baton.Tests;

public class BrowserReturnTests
{
    [Theory]
    [InlineData(HandoffStatus.Opened)]
    [InlineData(HandoffStatus.Fallback)]
    public async Task ActivatedTabIsFinalDestinationEvenWithoutPlayback(HandoffStatus status)
    {
        var diagnostics = new DiagnosticsLog();
        using var sessions = new MediaSessionSource(diagnostics, new LocalFiles());
        using var http = new HttpClient();
        var destination = new OpenResult(status, "Existing tab activated");
        var opener = new MediaOpener(sessions, new YouTubeResolver(http), diagnostics,
            resumeExisting: (_, _, _) => Task.FromResult<OpenResult?>(destination));
        Assert.Same(destination, await opener.TryOpenAsync(Phone("https://www.twitch.tv/trymacs"), CancellationToken.None));
        Assert.Equal(status == HandoffStatus.Opened,
            await opener.ReconcileAsync(Phone("https://www.twitch.tv/trymacs"), 95_000, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task LiveTwitchDoesNotFallBackToHistoricalSystemMediaSeek()
    {
        var diagnostics = new DiagnosticsLog();
        using var sessions = new MediaSessionSource(diagnostics, new LocalFiles());
        using var http = new HttpClient();
        var opener = new MediaOpener(sessions, new YouTubeResolver(http), diagnostics);
        Assert.False(await opener.ReconcileAsync(Phone("https://www.twitch.tv/trymacs"), 95_000, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/trymacs", "live")]
    [InlineData("https://www.twitch.tv/papaplatte", "live")]
    [InlineData(null, "live")]
    [InlineData("https://www.twitch.tv/videos/123456", "seek")]
    public async Task ReturnTargetsOriginalProfileAndDormantTabWithFreshContent(string? url, string mode)
    {
        await using var f = await Fixture.Start();
        var profile = Guid.NewGuid().ToString();
        var socket = await f.Connect(profile);
        var source = await f.Report(socket, "https://www.twitch.tv/trymacs");
        var target = f.Bridge.CaptureReturnTarget(source)!;
        f.Record(source, target);
        await f.Report(socket, null); // Dormant/discarded tabs are absent from reports.
        var activity = Phone(url);
        var opening = f.Bridge.TryResumeAsync(activity, 95_000);
        var request = await Receive(socket);
        Assert.Equal("resume", request.GetProperty("type").GetString());
        Assert.Equal(7, request.GetProperty("tabId").GetInt32());
        Assert.Equal(target.Url, request.GetProperty("expectedUrl").GetString());
        Assert.True(request.GetProperty("allowCreate").GetBoolean());
        Assert.Equal(mode, request.GetProperty("mode").GetString());
        var sentUrl = request.GetProperty("url").GetString()!;
        Assert.Equal(mode == "seek" ? "https://www.twitch.tv/videos/123456?t=0h1m35s" : url ?? target.Url, sentUrl);
        await Reply(socket, request, applied: false);
        Assert.Equal(HandoffStatus.Fallback, (await opening)!.Status);
        Assert.Equal(sentUrl, f.History.Find("continuation")!.Intent!.ReturnTab!.Url);
    }

    [Fact]
    public async Task ProfileReconnectRetainsDestinationAndOwnsResumeAcknowledgement()
    {
        await using var f = await Fixture.Start();
        var profile = Guid.NewGuid().ToString();
        var first = await f.Connect(profile);
        var source = await f.Report(first, "https://www.twitch.tv/trymacs");
        f.Record(source, f.Bridge.CaptureReturnTarget(source)!);
        first.Abort();
        var other = await f.Connect(Guid.NewGuid().ToString());
        var reconnected = await f.Connect(profile);
        var opening = f.Bridge.TryResumeAsync(Phone("https://www.twitch.tv/papaplatte"), 0);
        var request = await Receive(reconnected);
        await Reply(other, request, true);
        await Send(other, new { type = "hello", browser = "Test", version = "0.2.1" });
        await Receive(other); // Barrier: a reply from another connection must not resolve this request.
        Assert.False(opening.IsCompleted);
        await Reply(reconnected, request, true, tabId: 8);
        Assert.Equal(HandoffStatus.Opened, (await opening)!.Status);
        Assert.Equal(8, f.History.Find("continuation")!.Intent!.ReturnTab!.TabId);
    }

    [Fact]
    public async Task UncertainAcknowledgementPreventsDuplicatesWithoutChangingExpectedUrl()
    {
        await using var f = await Fixture.Start();
        var socket = await f.Connect(Guid.NewGuid().ToString());
        var source = await f.Report(socket, "https://www.twitch.tv/trymacs");
        f.Record(source, f.Bridge.CaptureReturnTarget(source)!);
        var opening = f.Bridge.TryResumeAsync(Phone("https://www.twitch.tv/papaplatte"), 0);
        await Receive(socket);
        Assert.Equal(HandoffStatus.Fallback, (await opening)!.Status);
        Assert.Equal(source.Url, f.History.Find("continuation")!.Intent!.ReturnTab!.Url);
    }

    [Fact]
    public async Task DifferentPhoneCannotAddressOriginalTabAndExplicitOtherBrowserIsHonored()
    {
        await using var f = await Fixture.Start();
        var socket = await f.Connect(Guid.NewGuid().ToString());
        var source = await f.Report(socket, "https://www.twitch.tv/trymacs");
        f.Record(source, f.Bridge.CaptureReturnTarget(source)!);
        var opening = f.Bridge.TryResumeAsync(Phone("https://www.twitch.tv/papaplatte") with { DeviceId = "other-phone" }, 0);
        var request = await Receive(socket);
        Assert.False(request.TryGetProperty("tabId", out _)); // Exact inventory search only, no source-tab access.
        Assert.False(request.GetProperty("allowCreate").GetBoolean());
        await Send(socket, new { type = "resumed", requestId = request.GetProperty("requestId").GetString(), activated = false, applied = false });
        Assert.Null(await opening);
        Assert.Null(await f.Bridge.TryResumeAsync(Phone(source.Url), 0, "another-browser.exe"));
    }

    [Fact]
    public async Task ChangedUnidentifiedNativeVideoDoesNotRestoreOldVideo()
    {
        await using var f = await Fixture.Start();
        var socket = await f.Connect(Guid.NewGuid().ToString());
        var source = await f.Report(socket, "https://www.youtube.com/watch?v=oldvideoid");
        f.Record(source, f.Bridge.CaptureReturnTarget(source)!);
        Assert.Null(await f.Bridge.TryResumeAsync(Phone(null) with { Title = "New video", Content = new ActivityContent("youtube") }, 0));
    }

    [Fact]
    public async Task LegacyPlaybackAcknowledgementAndPageReturnsRemainCompatible()
    {
        await using var f = await Fixture.Start();
        var socket = await f.Connect(null);
        var source = await f.Report(socket, "https://example.com/article");
        Assert.Null(f.Bridge.CaptureReturnTarget(source));
        var opening = f.Bridge.TryResumeAsync(Phone(source.Url) with { Playback = null, ContinuationId = null }, 0);
        var request = await Receive(socket);
        Assert.Equal("page", request.GetProperty("mode").GetString());
        await Send(socket, new { type = "resumed", requestId = request.GetProperty("requestId").GetString(), applied = true });
        Assert.Equal(HandoffStatus.Opened, (await opening)!.Status);
    }

    [Theory]
    [InlineData(HandoffStatus.Opened, true)]
    [InlineData(HandoffStatus.Fallback, true)]
    [InlineData(HandoffStatus.Failed, false)]
    [InlineData(null, false)]
    public void ContinuationsRequireSuccessfulSamePhoneHandoffAndExpireWithHistory(HandoffStatus? status, bool accepted)
    {
        var history = new HandoffHistory();
        var intent = new HandoffIntent("pc", "phone", "tab", HandoffModes.Auto, null,
            ReturnTab: new BrowserReturnTarget(Guid.NewGuid().ToString(), 7, "https://example.com"));
        history.Observe(new HandoffEvent("return", "pc", "phone", "Page", status, null), intent, DateTimeOffset.UnixEpoch);
        Assert.Equal(accepted, history.Continuation("return", "phone") is not null);
        Assert.Null(history.Continuation("return", "another-phone"));
        for (var i = 0; i < 40; i++) history.Observe(new HandoffEvent($"new{i}", "pc", "phone", "Page", null, null));
        Assert.Null(history.Continuation("return", "phone"));
    }

    [Theory]
    [InlineData("https://example.com/item?id=1", "https://example.com/item?id=2", false)]
    [InlineData("https://example.com/watch?v=abcdefg", "https://evil.test/watch?v=abcdefg", false)]
    [InlineData("https://example.com:8000/a", "https://example.com/a", false)]
    [InlineData("https://example.com/a?id=1&t=50&utm_source=test", "https://www.example.com/a?id=1", true)]
    [InlineData("https://m.twitch.tv/Trymacs", "https://www.twitch.tv/trymacs?t=1h", true)]
    public void ExactMatchingPreservesContentIdentity(string a, string b, bool expected) => Assert.Equal(expected, BrowserBridge.SamePage(a, b));

    private static Activity Phone(string? url) => new("media:twitch", "phone", ActivityKind.AppMedia, "Fresh channel",
        new ActivityApp("Twitch", "tv.twitch.android.app"), DateTimeOffset.UtcNow, Url: url,
        Playback: new Playback(95_000, 0, false, 1, DateTimeOffset.UtcNow), ContinuationId: "continuation");

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HttpClient _http = new();
        private readonly List<ClientWebSocket> _sockets = [];
        private WebApplication _app = null!;
        public BrowserBridge Bridge { get; }
        public HandoffHistory History { get; } = new();
        private Fixture() { Bridge = new(new DiagnosticsLog(), _http) { FindHandoff = History.Find, ReturnTabUpdated = History.UpdateReturnTab }; }
        public static async Task<Fixture> Start()
        {
            var f = new Fixture();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            f._app = builder.Build();
            f._app.UseWebSockets(); f._app.Run(f.Bridge.HandleAsync);
            await f._app.StartAsync();
            return f;
        }
        public async Task<ClientWebSocket> Connect(string? profile)
        {
            var socket = new ClientWebSocket(); _sockets.Add(socket);
            _profiles[socket] = profile;
            socket.Options.SetRequestHeader("Origin", BrowserBridge.Origin);
            await socket.ConnectAsync(new Uri(_app.Urls.Single().Replace("http:", "ws:") + "/browser"), CancellationToken.None);
            await Receive(socket);
            await Send(socket, new { type = "hello", browser = "Zen", version = "0.2.1", browserInstanceId = profile, tabReturn = profile is not null });
            await Receive(socket);
            return socket;
        }
        public async Task<Activity> Report(ClientWebSocket socket, string? url)
        {
            await Send(socket, new { type = "tabs", tabs = url is null ? Array.Empty<object>() : new object[] {
                new { tabId = 7, active = true, url, title = "Original", updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } } });
            await Send(socket, new { type = "hello", browser = "Zen", version = "0.2.1", browserInstanceId = GetProfile(socket), tabReturn = GetProfile(socket) is not null });
            await Receive(socket);
            return Bridge.Current.FirstOrDefault() ?? Phone(null);
        }
        private readonly Dictionary<ClientWebSocket, string?> _profiles = new();
        private string? GetProfile(ClientWebSocket socket) => _profiles.GetValueOrDefault(socket);
        public void Record(Activity source, BrowserReturnTarget target) => History.Observe(
            new HandoffEvent("continuation", "pc", "phone", source.Title, HandoffStatus.Opened, null),
            new HandoffIntent("pc", "phone", source.Id, HandoffModes.Auto, null, source, ReturnTab: target));
        public async ValueTask DisposeAsync()
        {
            foreach (var socket in _sockets) { socket.Abort(); socket.Dispose(); }
            await _app.StopAsync(); await _app.DisposeAsync(); _http.Dispose();
        }
    }
    private static async Task Reply(ClientWebSocket socket, JsonElement request, bool applied, int tabId = 7) =>
        await Send(socket, new { type = "resumed", requestId = request.GetProperty("requestId").GetString(), tabId, activated = true, applied });
    private static async Task Send(ClientWebSocket socket, object value) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Receive(ClientWebSocket socket)
    {
        var buffer = new byte[16384]; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.True(result.EndOfMessage);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }
}
