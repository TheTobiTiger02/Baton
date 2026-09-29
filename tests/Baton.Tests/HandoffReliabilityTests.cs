using System.Net.WebSockets;
using Baton.Host;
using Baton.Host.Browser;
using Baton.Host.Handoff;
using Baton.Protocol;

namespace Baton.Tests;

public class HandoffReliabilityTests
{
    [Theory]
    [InlineData(HandoffStatus.Opened)]
    [InlineData(HandoffStatus.Fallback)]
    [InlineData(HandoffStatus.Failed)]
    public void ResultsAreRetainedAndDeduplicated(HandoffStatus status)
    {
        var history = new HandoffHistory();
        var started = new HandoffEvent("id", "source", "target", "Video", null, "Sending…");
        history.Observe(started);
        Assert.True(history.Observe(started with { Status = status, Detail = "Result" }));
        Assert.False(history.Observe(started with { Status = status, Detail = "Result" }));
        Assert.False(history.Observe(started));
        Assert.Equal(status, Assert.Single(history.Recent).Event.Status);
    }

    [Fact]
    public void TimeoutRemainsUncertainAndLateResultResolvesSameRecord()
    {
        var history = new HandoffHistory();
        var now = DateTimeOffset.UtcNow;
        var value = new HandoffEvent("id", "a", "b", "Page", null, "Opening…");
        history.Observe(value, now: now);
        Assert.Empty(history.Expire(now.AddSeconds(119)));
        Assert.True(Assert.Single(history.Expire(now.AddMinutes(2))).Unconfirmed);
        Assert.Null(history.Find("id")!.Event.Status);
        Assert.Empty(history.Expire(now.AddMinutes(3)));
        Assert.True(history.Observe(value with { Status = HandoffStatus.Opened }));
        Assert.False(Assert.Single(history.Recent).Event.Unconfirmed);
        Assert.Equal(now, history.Recent[0].StartedAt);
    }

    [Fact]
    public void HistoryIsBoundedNewestFirstAndClearDoesNotResurrectOldRequests()
    {
        var history = new HandoffHistory();
        for (var i = 0; i < 45; i++) history.Observe(new HandoffEvent($"r{i}", "a", "b", "Page", null, null), now: DateTimeOffset.UnixEpoch.AddSeconds(i));
        Assert.Equal(40, history.Recent.Count);
        Assert.Equal("r44", history.Recent[0].Event.RequestId);
        Assert.Null(history.Find("r0"));
        Assert.False(history.Observe(new HandoffEvent("r0", "a", "b", "Late", HandoffStatus.Opened, null)));
        Assert.Equal("r44", history.Recent[0].Event.RequestId);
        history.Clear();
        Assert.False(history.Observe(new HandoffEvent("r44", "a", "b", "Page", HandoffStatus.Opened, null)));
        Assert.Empty(history.Recent);
    }

    [Fact]
    public void BrowserRoutesCannotCrossConnectionsAndAreBounded()
    {
        var routes = new BrowserHandoffRequests();
        var client = Guid.NewGuid().ToString();
        var first = routes.Add("browser-a", client);
        var second = routes.Add("browser-b", client);
        Assert.NotEqual(first, second);
        Assert.Equal(first, routes.HostId("browser-a", client));
        Assert.Null(routes.HostId("unapproved", client));
        Assert.Throws<ArgumentException>(() => routes.Add("browser-a", client));
        for (var i = 0; i < 40; i++) routes.Add("browser-a", Guid.NewGuid().ToString());
        Assert.Null(routes.Find(first));
        Assert.NotNull(routes.Find(second));
        routes.Remove("browser-b");
        Assert.Null(routes.Find(second));
    }

    [Fact]
    public void AutomaticWindowStreamingRetainsConsentErrorsButNativeAppChoiceDoesNotBecomeAStream()
    {
        var activity = new Activity("window", "pc", ActivityKind.WindowStream, "App", new ActivityApp("App", "app"), DateTimeOffset.UtcNow);
        var intent = new HandoffIntent("pc", "phone", activity.Id, HandoffModes.Auto, null, activity);
        Assert.True(intent.Streaming);
        Assert.False((intent with { Choice = new HandoffChoice(ChoiceKinds.App, "App", "app") }).Streaming);
    }

    [Fact]
    public async Task RetryUsesFreshOriginalActivityAndKeepsChoiceWithoutRememberingAgain()
    {
        await using var fixture = new Fixture();
        var phone = fixture.Pair("phone");
        var first = fixture.Source.Current[0];
        var choice = new HandoffChoice(ChoiceKinds.App, "Player", "player", Remember: true);
        await fixture.Coordinator.SendAsync(phone, first.Id, choice: choice);
        var record = Assert.Single(fixture.Coordinator.History.Recent);
        fixture.Coordinator.FailRequest(record.Event.RequestId, "Couldn't open it");
        fixture.Source.Set(first with { Playback = new Playback(44_000, 60_000, false, 1, DateTimeOffset.UtcNow) });
        await fixture.Coordinator.RecoverAsync(record.Event.RequestId);
        var next = fixture.Coordinator.History.Recent.First();
        Assert.NotEqual(record.Event.RequestId, next.Event.RequestId);
        Assert.Equal(first.Id, next.Intent!.ActivityId);
        Assert.Equal("player", next.Intent.Choice!.AppId);
        Assert.False(next.Intent.Choice.Remember);
        Assert.Equal(44_000, fixture.Source.Taken!.Playback!.PositionMs);
        fixture.Source.Set(first with { Url = "https://example.com/unrelated" });
        Assert.False(fixture.Coordinator.Recovery(record).Retry);
        fixture.Source.Set(first with { Id = "unrelated" });
        Assert.False(fixture.Coordinator.Recovery(record).Retry);
        Assert.Contains("original activity", fixture.Coordinator.Recovery(record).Reason);
        fixture.Host.Sessions.Detach(phone);
        Assert.Contains("Connect", fixture.Coordinator.Recovery(record).Reason);
    }

    [Fact]
    public async Task StreamRecoveryUsesCapabilityAndDoesNotOverrideNativeChoice()
    {
        await using var f = new Fixture();
        var phone = f.Pair("phone");
        await f.Coordinator.SendAsync(phone, "original");
        var id = f.Coordinator.History.Recent[0].Event.RequestId;
        f.Coordinator.FailRequest(id, "Native open failed");
        Assert.False(f.Coordinator.Recovery(f.Coordinator.History.Find(id)!).Stream);
        f.Source.Set(f.Source.Current[0] with { Window = new ActivityWindow("123", "player") });
        Assert.True(f.Coordinator.Recovery(f.Coordinator.History.Find(id)!).Stream);
        f.Coordinator.OfferStream = (activity, target, request) => new StreamOffer(request, 640, 480, activity.Title);
        await f.Coordinator.RecoverAsync(id, stream: true);
        var retry = f.Coordinator.History.Recent[0];
        Assert.Equal(ChoiceKinds.Stream, retry.Intent!.Choice!.Kind);
        Assert.False(retry.Intent.Choice.Remember);
    }

    [Fact]
    public async Task RelayRetainsBothPhonesAndOnlyDestinationMayReportSuccess()
    {
        await using var f = new Fixture();
        var a = f.Pair("a"); var b = f.Pair("b"); var other = f.Pair("other");
        var activity = f.Source.Current[0] with { DeviceId = a };
        await f.Host.DispatchAsync(a, Envelope.Create(MessageTypes.ActivityList, a, new ActivityListPayload([activity], PresenceState.Active)));
        var pull = new HandoffPullPayload("relay", a, b, activity.Id);
        await f.Host.DispatchAsync(b, Envelope.Create(MessageTypes.HandoffPull, b, pull));
        await f.Host.DispatchAsync(a, Envelope.Create(MessageTypes.HandoffDeliver, a, new HandoffDeliverPayload("relay", a, b, activity)));
        var result = new HandoffResultPayload("relay", a, b, HandoffStatus.Opened);
        await f.Host.DispatchAsync(other, Envelope.Create(MessageTypes.HandoffResult, other, result));
        Assert.Null(f.Coordinator.History.Find("relay")!.Event.Status);
        await f.Host.DispatchAsync(b, Envelope.Create(MessageTypes.HandoffResult, b, result));
        var record = f.Coordinator.History.Find("relay")!;
        Assert.Equal(a, record.Intent!.SourceDeviceId);
        Assert.Equal(b, record.Intent.TargetDeviceId);
        Assert.Equal(HandoffStatus.Opened, record.Event.Status);
    }

    [Fact]
    public async Task SpeculativeOpenCannotHideSourceFailure()
    {
        await using var f = new Fixture();
        var phone = f.Pair("phone");
        var activity = f.Source.Current[0] with { DeviceId = phone };
        await f.Host.DispatchAsync(phone, Envelope.Create(MessageTypes.ActivityList, phone, new ActivityListPayload([activity], PresenceState.Active)));
        await f.Coordinator.PullAsync(phone, activity.Id);
        var record = f.Coordinator.History.Recent[0];
        Assert.Null(record.Event.Status);
        await f.Host.DispatchAsync(phone, Envelope.Create(MessageTypes.HandoffResult, phone,
            new HandoffResultPayload(record.Event.RequestId, phone, f.Coordinator.LocalDeviceId, HandoffStatus.Failed, "Original activity closed")));
        Assert.Equal(HandoffStatus.Failed, f.Coordinator.History.Find(record.Event.RequestId)!.Event.Status);
    }

    [Theory]
    [InlineData(HandoffStatus.Opened)]
    [InlineData(HandoffStatus.Fallback)]
    public async Task DeliveryWaitsForSpeculativeOpenerAndDoesNotOpenTwice(HandoffStatus status)
    {
        var opener = new DeferredOpener();
        await using var f = new Fixture(opener);
        var phone = f.Pair("phone");
        var activity = f.Source.Current[0] with { DeviceId = phone, Playback = new Playback(20_000, 60_000, false, 1, DateTimeOffset.UtcNow) };
        await f.Host.DispatchAsync(phone, Envelope.Create(MessageTypes.ActivityList, phone, new ActivityListPayload([activity], PresenceState.Active)));
        await f.Coordinator.PullAsync(phone, activity.Id);
        var id = f.Coordinator.History.Recent[0].Event.RequestId;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Coordinator.History.Changed += () => { if (f.Coordinator.History.Find(id)?.Event.Status is not null) completed.TrySetResult(); };
        await f.Host.DispatchAsync(phone, Envelope.Create(MessageTypes.HandoffDeliver, phone,
            new HandoffDeliverPayload(id, phone, f.Coordinator.LocalDeviceId, activity)));
        await f.Host.DispatchAsync(phone, Envelope.Create(MessageTypes.HandoffDeliver, phone,
            new HandoffDeliverPayload(id, phone, f.Coordinator.LocalDeviceId, activity)));
        Assert.Null(f.Coordinator.History.Find(id)!.Event.Status);
        Assert.Equal(1, opener.Calls);
        opener.Result.SetResult(new OpenResult(status, "Original opener detail"));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, opener.Calls);
        Assert.Equal(status, f.Coordinator.History.Find(id)!.Event.Status);
        if (status == HandoffStatus.Fallback) Assert.Equal("Original opener detail", f.Coordinator.History.Find(id)!.Event.Detail);
        else Assert.Equal("Opened; playback position could not be confirmed.", f.Coordinator.History.Find(id)!.Event.Detail);
        Assert.Equal(activity.Id, f.Coordinator.History.Find(id)!.Intent!.ActivityId);
    }

    [Fact]
    public async Task ActivityDisappearingDuringFreshSnapshotFailsWithoutSendingCachedActivity()
    {
        await using var f = new Fixture();
        var phone = f.Pair("phone");
        f.Source.Unavailable = true;
        await f.Coordinator.SendAsync(phone, "original");
        var record = Assert.Single(f.Coordinator.History.Recent);
        Assert.Equal(HandoffStatus.Failed, record.Event.Status);
        Assert.Contains("no longer available", record.Event.Detail);
        Assert.Null(f.Source.Taken);
    }

    [Fact]
    public async Task StreamViewerOpenedDoesNotHideLaterAuthenticatedConsentFailure()
    {
        await using var f = new Fixture();
        var phone = f.Pair("phone");
        var activity = f.Source.Current[0] with { DeviceId = phone };
        await f.Host.DispatchAsync(phone, Envelope.Create(MessageTypes.HandoffDeliver, phone,
            new HandoffDeliverPayload("mirror", phone, f.Coordinator.LocalDeviceId, activity,
                new StreamOffer("mirror", 640, 480, activity.Title, StreamKinds.Phone))));
        Assert.Equal(HandoffStatus.Opened, f.Coordinator.History.Find("mirror")!.Event.Status);
        await f.Host.DispatchAsync(phone, Envelope.Create(MessageTypes.HandoffResult, phone,
            new HandoffResultPayload("mirror", phone, f.Coordinator.LocalDeviceId, HandoffStatus.Failed, "Consent declined")));
        Assert.Equal(HandoffStatus.Failed, f.Coordinator.History.Find("mirror")!.Event.Status);
        Assert.Equal("Consent declined", f.Coordinator.History.Find("mirror")!.Event.Detail);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public BatonHost Host { get; } = new(Path.Combine(Path.GetTempPath(), "BatonReliabilityTests", Guid.NewGuid().ToString("N")));
        public Source Source { get; } = new();
        public HandoffCoordinator Coordinator { get; }
        public Fixture(IActivityOpener? opener = null) => Coordinator = new(Host, [Source], [opener ?? new Opener()]);
        public string Pair(string id)
        {
            var ticket = Host.Devices.OpenPairingTicket();
            Assert.True(Host.Devices.ConfirmPairing(new PairHelloPayload(ticket.Code, id, id, "Test")).Accepted);
            Host.Sessions.Attach(id, new Socket());
            return id;
        }
        public async ValueTask DisposeAsync() { Coordinator.Dispose(); await Host.DisposeAsync(); }
    }

    private sealed class Source : IActivitySource
    {
        public event Action? Changed;
        public IReadOnlyList<Activity> Current { get; private set; } = [new("original", "pc", ActivityKind.WebMedia, "Video",
            new ActivityApp("Browser", "browser"), DateTimeOffset.UtcNow, Url: "https://example.com/video")];
        public Activity? Taken { get; private set; }
        public bool Unavailable { get; set; }
        public void Set(Activity value) { Current = [value]; Changed?.Invoke(); }
        public Task<Activity?> TakeAsync(string id, bool pause, CancellationToken cancellationToken) => Task.FromResult(Taken = Unavailable ? null : Current.FirstOrDefault(a => a.Id == id));
    }

    private sealed class Opener : IActivityOpener
    {
        public Task<OpenResult?> TryOpenAsync(Activity activity, CancellationToken cancellationToken) => Task.FromResult<OpenResult?>(OpenResult.Opened());
    }

    private sealed class DeferredOpener : IActivityOpener
    {
        public int Calls;
        public TaskCompletionSource<OpenResult?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OpenResult?> TryOpenAsync(Activity activity, CancellationToken cancellationToken) { Calls++; return Result.Task; }
    }

    private sealed class Socket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string? detail, CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? detail, CancellationToken cancellationToken) => CloseAsync(status, detail, cancellationToken);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
