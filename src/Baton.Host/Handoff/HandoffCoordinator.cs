using System.Collections.Concurrent;
using Baton.Host.Apps;
using Baton.Media;
using Baton.Protocol;

namespace Baton.Host.Handoff;

/// <summary>
/// The hub of the group. It knows every device's continuable activities, keeps each phone told
/// about everyone else's, and carries out handoffs: this PC's own activities through
/// <see cref="IActivitySource"/>s and <see cref="IActivityOpener"/>s, phone-to-phone ones by relaying.
///
/// Handoffs to this PC are speculative: every device's activities are already known here, so the
/// activity opens at once from what is known, while the source is asked for the exact position;
/// when that arrives, only the position is corrected.
/// </summary>
public sealed class HandoffCoordinator : IDisposable
{
    private static readonly TimeSpan BroadcastDebounce = TimeSpan.FromMilliseconds(150);
    private readonly BatonHost _host;
    private readonly IReadOnlyList<IActivitySource> _sources;
    private readonly IReadOnlyList<IActivityOpener> _openers;
    private readonly ActivityEnricher? _enricher;
    private readonly AppDirectory? _apps;
    private readonly ConcurrentDictionary<string, ActivityListPayload> _phoneActivities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (HandoffEvent Event, DateTimeOffset StartedAt)> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (Activity Activity, Task<OpenResult> Open)> _speculative = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _opening = new(StringComparer.Ordinal);
    private readonly Timer _expiry;
    public HandoffHistory History { get; } = new();
    private readonly HandoffSuggestions _suggestions = new();
    private readonly object _broadcastGate = new();
    private IReadOnlyList<Activity> _localActivities = [];
    private PresenceState _localPresence = PresenceState.Active;
    private bool _broadcastScheduled;

    public HandoffCoordinator(BatonHost host, IReadOnlyList<IActivitySource> sources, IReadOnlyList<IActivityOpener> openers,
        ActivityEnricher? enricher = null, AppDirectory? apps = null)
    {
        _host = host;
        _sources = sources;
        _openers = openers;
        _enricher = enricher;
        _apps = apps;
        if (apps is not null)
        {
            apps.PreferencesChanged += () => _ = PushPreferencesAsync(null);
        }
        foreach (var source in sources)
        {
            source.Changed += RefreshLocal;
        }

        if (enricher is not null)
        {
            enricher.Changed += RefreshLocal;
        }

        host.MessageReceived += OnMessageAsync;
        host.DeviceConnectionChanged += (deviceId, connected) =>
        {
            if (!connected)
            {
                _phoneActivities.TryRemove(deviceId, out _);
            }
            else
            {
                _ = PushPreferencesAsync(deviceId);
            }

            ScheduleBroadcast();
        };
        host.Devices.Changed += ScheduleBroadcast;
        host.DeviceNames.Changed += ScheduleBroadcast;
        RefreshLocal();
        _expiry = new Timer(_ => ExpirePending(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Dispose() => _expiry.Dispose();
    private void ExpirePending()
    {
        foreach (var expired in History.Expire(DateTimeOffset.UtcNow))
            HandoffUpdated?.Invoke(expired);
        foreach (var item in _pending.Where(pair => DateTimeOffset.UtcNow - pair.Value.StartedAt >= HandoffHistory.PendingLifetime))
        {
            _pending.TryRemove(item.Key, out _);
        }
    }

    public (bool Retry, bool Stream, string? Reason) Recovery(HandoffRecord record)
    {
        if (record.Intent is not { } intent) return (false, false, "This transfer has no recoverable activity.");
        var devices = GetDevices();
        if (devices.FirstOrDefault(d => d.DeviceId == intent.SourceDeviceId)?.Online != true ||
            devices.FirstOrDefault(d => d.DeviceId == intent.TargetDeviceId)?.Online != true)
            return (false, false, "Connect both devices to recover this handoff.");
        var activity = intent.Standalone ? intent.Activity : FindActivity(intent.SourceDeviceId, intent.ActivityId);
        if (!intent.Standalone && activity is not null && intent.Activity is { } original &&
            (original.Url is not null && activity.Url is not null
                ? !Baton.Host.Browser.BrowserBridge.SamePage(original.Url, activity.Url)
                : original.Title != activity.Title)) activity = null;
        if (activity is null) return (false, false, "The original activity is no longer available.");
        var canStream = intent.SourceDeviceId == LocalDeviceId ? CanStream(activity) :
            intent.TargetDeviceId == LocalDeviceId && devices.FirstOrDefault(d => d.DeviceId == intent.SourceDeviceId)?.Kind == DeviceKinds.Phone;
        return (record.Recoverable, canStream && (record.Recoverable || record.Event.Status == HandoffStatus.Fallback), null);
    }

    public async Task RecoverAsync(string requestId, bool stream = false, string? newRequestId = null)
    {
        var record = History.Find(requestId);
        if (record?.Intent is not { } intent) return;
        var allowed = Recovery(record);
        if (!(stream ? allowed.Stream : allowed.Retry))
        {
            if (newRequestId is not null) Report(new HandoffEvent(newRequestId, intent.SourceDeviceId, intent.TargetDeviceId,
                record.Event.Title, HandoffStatus.Failed, allowed.Reason ?? "This handoff cannot be retried."));
            return;
        }
        var mode = stream ? HandoffModes.Stream : intent.Mode;
        var choice = stream ? new HandoffChoice(ChoiceKinds.Stream, "Stream") : intent.Choice is { } chosen ? chosen with { Remember = false } : null;
        newRequestId ??= NewRequestId();
        try
        {
            if (intent.SourceDeviceId == LocalDeviceId)
            {
                if (intent.Standalone && intent.Activity is { } activity) await SendActivityAsync(intent.TargetDeviceId, activity, mode, choice, newRequestId);
                else await SendAsync(intent.TargetDeviceId, intent.ActivityId, mode, choice, newRequestId);
            }
            else await PullAsync(intent.SourceDeviceId, intent.ActivityId, intent.TargetDeviceId, mode, choice, newRequestId);
        }
        catch (Exception ex) { Finish(newRequestId, HandoffStatus.Failed, ex.Message); }
    }

    /// <summary>The group changed: devices, their activities or presence. Any thread.</summary>
    public event Action? DevicesChanged;

    /// <summary>A handoff started, progressed or finished. Any thread.</summary>
    public event Action<HandoffEvent>? HandoffUpdated;

    /// <summary>
    /// A phone is streaming its screen to this PC: (phone, activity, stream). The app shows it in a
    /// window. Any thread.
    /// </summary>
    public event Action<string, Activity, StreamOffer>? PhoneStreamOffered;

    public string LocalDeviceId => _host.Identity.HostId;

    /// <summary>
    /// Starts a live stream of an activity's window to a device: (activity, target, session id).
    /// Set by the runtime; without it, activities that can only be streamed cannot be handed over.
    /// </summary>
    public Func<Activity, string, string, StreamOffer?>? OfferStream { get; set; }

    /// <summary>
    /// Pauses media titled like the activity by every other means this PC has (a tab through the
    /// extension, a system media session): used when pausing it at its source did not take. Set by
    /// the runtime.
    /// </summary>
    public Func<Activity, Task<bool>>? PauseElsewhere { get; set; }

    /// <summary>
    /// Opens web media or a page in a given browser at its position, the way the default choice
    /// does (continuing the tab that still has it, else a link with its time, then a seek):
    /// (activity, browser program). Null when it can't handle the activity. Set by the runtime.
    /// </summary>
    public Func<Activity, string, Task<OpenResult?>>? OpenInBrowser { get; set; }

    /// <summary>
    /// Activities that continue as a stream of their window rather than by reopening the content:
    /// windows themselves, and media apps no other device has.
    /// </summary>
    public static bool ContinuesAsStream(Activity activity) =>
        activity.Kind == ActivityKind.WindowStream || (activity.Content?.Provider == "unknown" && activity.Window is not null);

    /// <summary>Whether an activity can be streamed at all ("Stream instead").</summary>
    public static bool CanStream(Activity activity) => activity.Window is not null;

    public IReadOnlyList<Activity> LocalActivities => _localActivities;

    public AppDirectory? Apps => _apps;

    private string PlatformOf(string deviceId) => deviceId == LocalDeviceId ? Platforms.Windows : Platforms.Android;

    /// <summary>The ways an activity on <paramref name="sourceDeviceId"/> can continue on <paramref name="targetDeviceId"/>, best first.</summary>
    public IReadOnlyList<HandoffChoice> Options(Activity activity, string sourceDeviceId, string targetDeviceId) =>
        _apps?.Options(activity, PlatformOf(sourceDeviceId), PlatformOf(targetDeviceId), targetDeviceId) ?? [];

    /// <summary>What the user chose for this app and direction before, if anything.</summary>
    public HandoffChoice? Remembered(Activity activity, string sourceDeviceId, string targetDeviceId) =>
        _apps?.Remembered(activity, PlatformOf(sourceDeviceId), PlatformOf(targetDeviceId), targetDeviceId);

    /// <summary>
    /// The choice to use when none was given: the remembered one, else for an app (which has no
    /// content to reopen) the best match, so a phone app opens its PC twin rather than a mirror.
    /// </summary>
    private HandoffChoice? Implicit(Activity activity, string sourceDeviceId, string targetDeviceId) =>
        Remembered(activity, sourceDeviceId, targetDeviceId)
        ?? (activity.Kind == ActivityKind.WindowStream ? Options(activity, sourceDeviceId, targetDeviceId).FirstOrDefault() : null);

    private void RememberIfAsked(Activity? activity, string sourceDeviceId, string targetDeviceId, HandoffChoice? choice)
    {
        if (activity is not null && choice is { Remember: true })
        {
            _apps?.Remember(activity, PlatformOf(sourceDeviceId), PlatformOf(targetDeviceId), targetDeviceId, choice);
        }
    }

    private static bool Streams(HandoffChoice? choice, string mode, Activity activity) => choice?.Kind switch
    {
        ChoiceKinds.Stream => true,
        ChoiceKinds.App or ChoiceKinds.Web => false,
        _ => mode == HandoffModes.Stream || ContinuesAsStream(activity)
    };

    private async Task PushPreferencesAsync(string? deviceId)
    {
        if (_apps is null)
        {
            return;
        }

        var payload = new AppPreferencesPayload(_apps.Preferences);
        var recipients = deviceId is null
            ? _host.Devices.GetDevices().Select(device => device.DeviceId).Where(_host.IsConnected).ToArray()
            : [deviceId];
        foreach (var recipient in recipients)
        {
            await _host.SendAsync(recipient, MessageTypes.AppPreferences, payload);
        }
    }

    /// <summary>Every device, this PC first, then phones by name.</summary>
    public IReadOnlyList<DeviceView> GetDevices()
    {
        var devices = new List<DeviceView>
        {
            new(LocalDeviceId, _host.Identity.PcName, DeviceKinds.Pc, true, _localPresence, _localActivities)
        };
        foreach (var phone in _host.Devices.GetDevices())
        {
            var online = _host.IsConnected(phone.DeviceId);
            var list = online && _phoneActivities.TryGetValue(phone.DeviceId, out var payload) ? payload : null;
            devices.Add(new DeviceView(
                phone.DeviceId,
                _host.DeviceNames.Get(phone.DeviceId) ?? phone.DisplayName,
                DeviceKinds.Phone,
                online,
                list?.Presence ?? PresenceState.Idle,
                list is null ? [] : _enricher?.Enrich(list.Activities) ?? list.Activities));
        }

        return devices;
    }

    /// <summary>
    /// What to offer someone returning to this PC (see <see cref="HandoffSuggestions.Pick"/>), or
    /// null. An activity is offered once per <see cref="HandoffSuggestions.RepeatAfter"/>.
    /// </summary>
    public (DeviceView Device, Activity Activity)? SuggestionForReturn()
    {
        var now = DateTimeOffset.UtcNow;
        var best = GetDevices()
            .Where(device => device.Kind == DeviceKinds.Phone && device.Online)
            .Select(device => (Device: device, Activity: HandoffSuggestions.Pick(device.Activities, device.Presence, now)))
            .Where(pair => pair.Activity is not null)
            .OrderByDescending(pair => pair.Activity!.Playback?.Playing == true)
            .ThenByDescending(pair => pair.Activity!.UpdatedAt)
            .FirstOrDefault();
        return best.Activity is { } activity && _suggestions.TryOffer(best.Device.DeviceId, activity, now)
            ? (best.Device, activity)
            : null;
    }

    public void SetLocalPresence(PresenceState presence)
    {
        if (_localPresence == presence)
        {
            return;
        }

        _localPresence = presence;
        ScheduleBroadcast();
    }

    /// <summary>Hands one of this PC's activities (the top one when null) to a phone.</summary>
    public Task SendAsync(string targetDeviceId, string? activityId = null, string mode = HandoffModes.Auto, HandoffChoice? choice = null, string? requestId = null) =>
        DeliverLocalAsync(requestId ?? NewRequestId(), targetDeviceId, activityId, mode, choice);
    /// <summary>
    /// Asks a device for its activity (its top one when null), to continue on
    /// <paramref name="targetDeviceId"/> (this PC when null). When it continues here, it opens
    /// right away from what is already known, and is corrected when the exact position arrives.
    /// </summary>
    public async Task PullAsync(string sourceDeviceId, string? activityId = null, string? targetDeviceId = null, string mode = HandoffModes.Auto,
        HandoffChoice? choice = null, string? requestId = null)
    {
        var target = targetDeviceId ?? LocalDeviceId;
        var known = FindActivity(sourceDeviceId, activityId);
        if (known is not null && mode == HandoffModes.Auto)
        {
            choice ??= Implicit(known, sourceDeviceId, target);
        }

        RememberIfAsked(known, sourceDeviceId, target, choice);
        if (choice?.Kind == ChoiceKinds.Stream)
        {
            mode = HandoffModes.Stream;
        }

        // Opens here at once from what is known: the content (corrected when the exact position
        // arrives), or the app the user picked, which needs nothing from the phone at all.
        var speculative = target == LocalDeviceId && known is not null && !Streams(choice, mode, known);
        var request = new HandoffPullPayload(requestId ?? NewRequestId(), sourceDeviceId, target, activityId ?? known?.Id, speculative, mode, choice);
        _host.Timeline.Mark(request.RequestId, "pull");
        Track(new HandoffEvent(request.RequestId, sourceDeviceId, target, known?.Title ?? "Activity", null, "Asking the phone…"),
            new HandoffIntent(sourceDeviceId, target, request.ActivityId, mode, choice, known));
        if (speculative)
        {
            _speculative[request.RequestId] = (known!, OpenHereAsync(request.RequestId, sourceDeviceId, known!, speculative: true, choice));
        }

        if (!await _host.SendWhenConnectedAsync(sourceDeviceId, MessageTypes.HandoffPull, request))
        {
            Finish(request.RequestId, HandoffStatus.Failed, "The phone is not connected.");
        }
    }

    /// <summary>Hands over an activity that no source tracks, such as a link picked from a context menu.</summary>
    public async Task SendActivityAsync(string targetDeviceId, Activity activity, string mode = HandoffModes.Auto, HandoffChoice? choice = null, string? requestId = null)
    {
        requestId ??= NewRequestId();
        _host.Timeline.Mark(requestId, "send");
        Track(new HandoffEvent(requestId, LocalDeviceId, targetDeviceId, activity.Title, null, "Sending…"),
            new HandoffIntent(LocalDeviceId, targetDeviceId, activity.Id, mode, choice, activity, true));
        await DeliverAsync(requestId, targetDeviceId, activity with { DeviceId = LocalDeviceId }, mode, choice);
    }

    /// <summary>
    /// Plays, pauses, seeks or changes the volume of an activity on any device: this PC's own
    /// through its source, a phone's by asking the phone.
    /// </summary>
    public async Task<bool> CommandAsync(MediaCommandPayload command)
    {
        if (command.OwnerDeviceId != LocalDeviceId)
        {
            return await _host.SendAsync(command.OwnerDeviceId, MessageTypes.MediaCommand, command);
        }

        var activity = _localActivities.FirstOrDefault(item => item.Id == command.ActivityId);
        if (activity is null)
        {
            return false;
        }

        foreach (var source in _sources.OfType<IActivityControl>())
        {
            if (((IActivitySource)source).Current.Any(item => item.Id == activity.Id)
                && await source.CommandAsync(activity, command, CancellationToken.None))
            {
                return true;
            }
        }

        return false;
    }

    private async Task DeliverLocalAsync(string requestId, string targetDeviceId, string? activityId, string mode, HandoffChoice? choice = null)
    {
        _host.Timeline.Mark(requestId, "deliver requested");
        var candidate = activityId is null
            ? _localActivities.FirstOrDefault()
            : _localActivities.FirstOrDefault(activity => activity.Id == activityId);
        if (candidate is null)
        {
            Report(new HandoffEvent(requestId, LocalDeviceId, targetDeviceId, "Nothing to continue",
                HandoffStatus.Failed, "Nothing is playing or open on this PC right now."));
            await _host.SendAsync(targetDeviceId, MessageTypes.HandoffResult, new HandoffResultPayload(
                requestId, LocalDeviceId, targetDeviceId, HandoffStatus.Failed, "Nothing is playing or open on the PC right now."));
            return;
        }

        if (mode == HandoffModes.Auto)
        {
            choice ??= Implicit(candidate, LocalDeviceId, targetDeviceId);
        }

        Track(new HandoffEvent(requestId, LocalDeviceId, targetDeviceId, candidate.Title, null, "Sending…"),
            new HandoffIntent(LocalDeviceId, targetDeviceId, candidate.Id, mode, choice, candidate));
        RememberIfAsked(candidate, LocalDeviceId, targetDeviceId, choice);
        var stream = Streams(choice, mode, candidate);
        var source = _sources.FirstOrDefault(item => item.Current.Any(activity => activity.Id == candidate.Id));
        Activity? activity;
        try
        {
            // A streamed activity keeps running here: the phone watches this very window.
            activity = source is null ? candidate : await source.TakeAsync(candidate.Id, pause: !stream, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "take.failed", ex.Message, severity: DiagnosticsSeverity.Warning);
            Finish(requestId, HandoffStatus.Failed, "Couldn't obtain a fresh activity snapshot.");
            await _host.SendAsync(targetDeviceId, MessageTypes.HandoffResult, new HandoffResultPayload(
                requestId, LocalDeviceId, targetDeviceId, HandoffStatus.Failed, "Couldn't obtain a fresh activity snapshot."));
            return;
        }

        if (activity is null)
        {
            Finish(requestId, HandoffStatus.Failed, "The original activity is no longer available.");
            await _host.SendAsync(targetDeviceId, MessageTypes.HandoffResult, new HandoffResultPayload(
                requestId, LocalDeviceId, targetDeviceId, HandoffStatus.Failed, "The original activity is no longer available."));
            return;
        }
        _host.Timeline.Mark(requestId, "taken");
        activity = activity with { DeviceId = LocalDeviceId, Window = activity.Window ?? candidate.Window };
        if (!stream && candidate.Playback is { Playing: true } playing)
        {
            // The source's answer lost the player (a tab whose page script isn't there): the
            // position it had a moment ago beats starting over.
            if (activity.Playback is null)
            {
                var now = DateTimeOffset.UtcNow;
                activity = activity with { Playback = playing with { PositionMs = playing.PositionAt(now), CapturedAt = now } };
            }

            // Nothing may keep playing here once it continues elsewhere.
            if (activity.Playback is not { Playing: false })
            {
                var paused = PauseElsewhere is not null && await PauseElsewhere(activity);
                _host.Diagnostics.Record(DiagnosticsCategory.Handoff, paused ? "take.pause-fallback" : "take.not-paused", activity.Title,
                    severity: paused ? DiagnosticsSeverity.Info : DiagnosticsSeverity.Warning);
                activity = activity with { Playback = activity.Playback! with { Playing = false } };
            }
        }

        await DeliverAsync(requestId, targetDeviceId, _enricher?.Enrich(activity) ?? activity, stream ? HandoffModes.Stream : HandoffModes.Auto, choice);
    }

    /// <summary>Sends a ready activity, with a stream of its window when that is how it continues.</summary>
    private async Task DeliverAsync(string requestId, string targetDeviceId, Activity activity, string mode, HandoffChoice? choice = null)
    {
        StreamOffer? offer = null;
        if (Streams(choice, mode, activity))
        {
            try
            {
                offer = OfferStream?.Invoke(activity, targetDeviceId, requestId);
            }
            catch (Exception ex)
            {
                _host.Diagnostics.Record(DiagnosticsCategory.Stream, "offer.failed", ex.Message, severity: DiagnosticsSeverity.Warning);
            }

            if (offer is null)
            {
                Finish(requestId, HandoffStatus.Failed, "This window can't be streamed.");
                await _host.SendAsync(targetDeviceId, MessageTypes.HandoffResult, new HandoffResultPayload(
                    requestId, LocalDeviceId, targetDeviceId, HandoffStatus.Failed, "This window can't be streamed."));
                return;
            }
        }

        var deliver = new HandoffDeliverPayload(requestId, LocalDeviceId, targetDeviceId, activity, offer, offer is null ? choice : null);
        _host.Timeline.Mark(requestId, "deliver sent");
        _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "deliver.sent", $"{activity.Kind} '{activity.Title}'", targetDeviceId);
        if (!await _host.SendWhenConnectedAsync(targetDeviceId, MessageTypes.HandoffDeliver, deliver))
        {
            Finish(requestId, HandoffStatus.Failed, "The phone is not connected.");
        }
    }

    private async Task OnMessageAsync(string deviceId, Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageTypes.ActivityList:
            {
                var list = envelope.ReadRequired<ActivityListPayload>();
                _phoneActivities[deviceId] = list with { Activities = list.Activities.Select(activity => activity.Normalized()).ToArray() };
                ScheduleBroadcast();
                break;
            }

            case MessageTypes.HandoffPull:
            {
                var pull = envelope.ReadRequired<HandoffPullPayload>();
                if (deviceId != pull.SourceDeviceId && deviceId != pull.TargetDeviceId) break;
                _host.Timeline.Mark(pull.RequestId, $"pull from {deviceId[..Math.Min(12, deviceId.Length)]}");
                var known = FindActivity(pull.SourceDeviceId, pull.ActivityId);
                Track(new HandoffEvent(pull.RequestId, pull.SourceDeviceId, pull.TargetDeviceId, known?.Title ?? "Activity", null, "Requesting activity…"),
                    new HandoffIntent(pull.SourceDeviceId, pull.TargetDeviceId, pull.ActivityId ?? known?.Id, pull.Mode, pull.Choice, known));
                RememberIfAsked(known, pull.SourceDeviceId, pull.TargetDeviceId, pull.Choice);
                if (pull.SourceDeviceId == LocalDeviceId)
                {
                    await DeliverLocalAsync(pull.RequestId, pull.TargetDeviceId, pull.ActivityId, pull.Mode, pull.Choice);
                }
                else if (!await _host.SendWhenConnectedAsync(pull.SourceDeviceId, MessageTypes.HandoffPull, pull))
                {
                    Finish(pull.RequestId, HandoffStatus.Failed, "That device is offline.");
                    await _host.SendAsync(deviceId, MessageTypes.HandoffResult, new HandoffResultPayload(
                        pull.RequestId, pull.SourceDeviceId, pull.TargetDeviceId, HandoffStatus.Failed, "That device is offline."));
                }

                break;
            }

            case MessageTypes.HandoffDeliver:
            {
                var received = envelope.ReadRequired<HandoffDeliverPayload>();
                if (received.SourceDeviceId != deviceId) break;
                var deliver = received with { Activity = received.Activity.Normalized() };
                var original = History.Find(deliver.RequestId)?.Event;
                if (original is not null && (original.SourceDeviceId != deliver.SourceDeviceId || original.TargetDeviceId != deliver.TargetDeviceId)) break;
                if (History.Find(deliver.RequestId)?.Event.Status is { } finished)
                {
                    await _host.SendAsync(deviceId, MessageTypes.HandoffResult, new HandoffResultPayload(deliver.RequestId,
                        deliver.SourceDeviceId, deliver.TargetDeviceId, finished, History.Find(deliver.RequestId)?.Event.Detail));
                    break;
                }
                Track(new HandoffEvent(deliver.RequestId, deliver.SourceDeviceId, deliver.TargetDeviceId, deliver.Activity.Title, null, "Activity received…"),
                    History.Find(deliver.RequestId)?.Intent is { } originalIntent
                        ? originalIntent with { ActivityId = originalIntent.ActivityId ?? deliver.Activity.Id, Activity = deliver.Activity }
                        : new HandoffIntent(deliver.SourceDeviceId, deliver.TargetDeviceId, deliver.Activity.Id,
                            deliver.Stream is null ? HandoffModes.Auto : HandoffModes.Stream, deliver.Choice, deliver.Activity));
                RememberIfAsked(deliver.Activity, deliver.SourceDeviceId, deliver.TargetDeviceId, deliver.Choice);
                if (deliver.TargetDeviceId == LocalDeviceId)
                {
                    if (_opening.TryAdd(deliver.RequestId, 0)) _ = ReceiveHereAsync(deliver);
                }
                else if (!await _host.SendWhenConnectedAsync(deliver.TargetDeviceId, MessageTypes.HandoffDeliver, deliver))
                {
                    Finish(deliver.RequestId, HandoffStatus.Failed, "That device is offline.");
                    await _host.SendAsync(deviceId, MessageTypes.HandoffResult, new HandoffResultPayload(
                        deliver.RequestId, deliver.SourceDeviceId, deliver.TargetDeviceId, HandoffStatus.Failed, "That device is offline."));
                }

                break;
            }

            case MessageTypes.HandoffResult:
            {
                var result = envelope.ReadRequired<HandoffResultPayload>();
                var original = History.Find(result.RequestId)?.Event ?? (_pending.TryGetValue(result.RequestId, out var active) ? active.Event : null);
                if (original is null || original.SourceDeviceId != result.SourceDeviceId || original.TargetDeviceId != result.TargetDeviceId ||
                    (deviceId != result.TargetDeviceId && !(deviceId == result.SourceDeviceId && result.Status == HandoffStatus.Failed))) break;
                _host.Timeline.Mark(result.RequestId, $"result {result.Status}");
                if (result.OpenedMs is { } openedMs)
                {
                    _host.Timeline.Mark(result.RequestId, "target opened (target clock)", openedMs);
                }

                _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "result", $"{result.Status}: {result.Detail}", deviceId);
                if (original is not null)
                {
                    Finish(result.RequestId, result.Status, result.Detail);
                }

                if (result.SourceDeviceId != LocalDeviceId && result.SourceDeviceId != deviceId)
                {
                    await _host.SendAsync(result.SourceDeviceId, MessageTypes.HandoffResult, result);
                }

                if (result.TargetDeviceId != LocalDeviceId && result.TargetDeviceId != deviceId)
                {
                    await _host.SendAsync(result.TargetDeviceId, MessageTypes.HandoffResult, result);
                }

                break;
            }

            case MessageTypes.HandoffOptions:
            {
                var request = envelope.ReadRequired<HandoffOptionsRequest>();
                var source = request.Activity.DeviceId is { Length: > 0 } owner ? owner : deviceId;
                await _host.SendAsync(deviceId, MessageTypes.HandoffOptions, new HandoffOptionsPayload(
                    request.RequestId,
                    Options(request.Activity, source, request.TargetDeviceId),
                    Remembered(request.Activity, source, request.TargetDeviceId)));
                break;
            }

            case MessageTypes.AppCatalog:
                _apps?.SetPhoneApps(deviceId, envelope.ReadRequired<AppCatalogPayload>().Apps);
                break;

            case MessageTypes.AppPreferenceRemove:
                _apps?.Forget(envelope.ReadRequired<AppPreferenceRemovePayload>().Key);
                break;

            case MessageTypes.MediaCommand:
            {
                var command = envelope.ReadRequired<MediaCommandPayload>();
                if (command.OwnerDeviceId == LocalDeviceId || command.OwnerDeviceId != deviceId)
                {
                    await CommandAsync(command);
                }

                break;
            }
        }
    }

    private async Task OpenAndReportAsync(HandoffDeliverPayload deliver)
    {
        // The video id is usually known already (looked up for the peer list): no web lookup now.
        var activity = _enricher?.Enrich(deliver.Activity) ?? deliver.Activity;
        var result = await OpenHereAsync(deliver.RequestId, deliver.SourceDeviceId, activity, speculative: false, deliver.Choice);
        await _host.SendAsync(deliver.SourceDeviceId, MessageTypes.HandoffResult, new HandoffResultPayload(
            deliver.RequestId, deliver.SourceDeviceId, LocalDeviceId, result.Status, result.Detail));
    }

    private async Task ReceiveHereAsync(HandoffDeliverPayload deliver)
    {
        try
        {
            _host.Timeline.Mark(deliver.RequestId, "deliver received");
            if (deliver.Stream is { Kind: StreamKinds.Phone } phoneStream)
            {
                PhoneStreamOffered?.Invoke(deliver.SourceDeviceId, deliver.Activity, phoneStream);
                const string detail = "Screen viewer opened; picture follows after screen-sharing consent.";
                Finish(deliver.RequestId, HandoffStatus.Opened, detail);
                await _host.SendAsync(deliver.SourceDeviceId, MessageTypes.HandoffResult,
                    new HandoffResultPayload(deliver.RequestId, deliver.SourceDeviceId, LocalDeviceId, HandoffStatus.Opened, detail));
            }
            else if (_speculative.TryRemove(deliver.RequestId, out var opened))
                await CompleteSpeculativeAsync(deliver, opened.Activity, opened.Open);
            else await OpenAndReportAsync(deliver);
        }
        catch (Exception ex)
        {
            Finish(deliver.RequestId, HandoffStatus.Failed, ex.Message);
            await _host.SendAsync(deliver.SourceDeviceId, MessageTypes.HandoffResult,
                new HandoffResultPayload(deliver.RequestId, deliver.SourceDeviceId, LocalDeviceId, HandoffStatus.Failed, ex.Message));
        }
        finally { _opening.TryRemove(deliver.RequestId, out _); }
    }

    private async Task<OpenResult> OpenHereAsync(string requestId, string sourceDeviceId, Activity activity, bool speculative, HandoffChoice? choice = null)
    {
        Track(new HandoffEvent(requestId, sourceDeviceId, LocalDeviceId, activity.Title, null, "Opening…"));
        _host.Diagnostics.Record(DiagnosticsCategory.Handoff, speculative ? "open.speculative" : "deliver.received",
            $"{activity.Kind} '{activity.Title}' {activity.Url} as {choice?.Kind} {choice?.Label}", sourceDeviceId);

        choice ??= Implicit(activity, sourceDeviceId, LocalDeviceId);
        OpenResult result = choice?.Kind == ChoiceKinds.Stream
            ? OpenResult.Failed("Show it on the PC from the phone: tap Mirror there.")
            : OpenResult.Failed("Baton doesn't know how to continue this here yet.");
        if (choice is { Kind: ChoiceKinds.App or ChoiceKinds.Web } && _apps is not null)
        {
            try
            {
                // A browser picked for it opens it the same way the default does: at its position,
                // in the tab that still has it when there is one.
                if (_apps.BrowserExeFor(choice) is { } browser && activity.Url is not null && OpenInBrowser is { } openIn
                    && await openIn(activity, browser) is { } opened)
                {
                    result = opened;
                }
                // Media coming back to an app picked for it continues where it is.
                else if (choice.Kind == ChoiceKinds.App && activity.Playback is { } playback && _apps.ResumeExisting is { } resume
                    && await resume(activity, playback.PositionAt(DateTimeOffset.UtcNow)))
                {
                    result = OpenResult.Opened("Continued where it was on this PC");
                }
                else
                {
                    var status = _apps.Open(activity, choice, out var detail);
                    result = new OpenResult(status, detail);
                }
            }
            catch (Exception ex)
            {
                _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "open.failed", $"{choice.Label}: {ex.Message}", severity: DiagnosticsSeverity.Warning);
                result = OpenResult.Failed($"Couldn't open {choice.Label}: {ex.Message}");
            }
        }
        else if (choice?.Kind != ChoiceKinds.Stream)
        {
            foreach (var opener in _openers)
            {
                try
                {
                    if (await opener.TryOpenAsync(activity, CancellationToken.None) is { } opened)
                    {
                        result = opened;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _host.Diagnostics.Record(DiagnosticsCategory.Handoff, "open.failed", $"{opener.GetType().Name}: {ex.Message}",
                        severity: DiagnosticsSeverity.Warning);
                    result = OpenResult.Failed(ex.Message);
                    break;
                }
            }
        }

        _host.Timeline.Mark(requestId, speculative ? "opened (speculative)" : "opened");
        if (!speculative) Finish(requestId, result.Status, result.Detail);
        else Track(new HandoffEvent(requestId, sourceDeviceId, LocalDeviceId, activity.Title, null,
            result.Status == HandoffStatus.Failed ? "Waiting for the source to retry opening…" : "Opened here; waiting for the source snapshot…"));
        return result;
    }

    /// <summary>
    /// The exact snapshot of something already opened here arrived: move to its position if the
    /// guess was off. Anything else about it is what was opened.
    /// </summary>
    private async Task<bool> ReconcileAsync(string requestId, Activity opened, Activity exact)
    {
        var now = DateTimeOffset.UtcNow;
        var guessed = opened.Playback?.PositionAt(now);
        var actual = exact.Playback?.PositionAt(now);
        if (guessed is null || actual is null || Math.Abs(guessed.Value - actual.Value) <= 2_000)
        {
            _host.Timeline.Mark(requestId, "no position correction requested");
            // Matching source snapshots do not confirm the destination player's position.
            return false;
        }

        _host.Timeline.Mark(requestId, $"correcting position by {(actual - guessed) / 1000} s");
        foreach (var opener in _openers.OfType<IPositionReconciler>())
        {
            if (await opener.ReconcileAsync(exact, actual.Value, CancellationToken.None))
            {
                _host.Timeline.Mark(requestId, "position corrected");
                return true;
            }
        }
        return false;
    }

    private Activity? FindActivity(string deviceId, string? activityId)
    {
        var activities = GetDevices().FirstOrDefault(device => device.DeviceId == deviceId)?.Activities ?? [];
        return activityId is null ? activities.FirstOrDefault() : activities.FirstOrDefault(activity => activity.Id == activityId);
    }

    private void RefreshLocal()
    {
        // Focus is read here, at ranking time, so it is current for every source; the foreground
        // window changing re-ranks through the window source. Browser tabs bring their own.
        var foreground = DesktopWindows.Foreground() is { } window ? DesktopWindows.Token(window.Handle) : null;
        var ranked = ActivityRanking.Rank(
            // Windows media sessions report absurd lengths for live streams too, like phones do.
            _sources.SelectMany(source => source.Current).Select(activity => activity.Normalized() with
            {
                DeviceId = LocalDeviceId,
                Focused = activity.Window is { } owner ? owner.WindowToken == foreground : activity.Focused
            }),
            DateTimeOffset.UtcNow);
        _localActivities = _enricher?.Enrich(ranked) ?? ranked;
        ScheduleBroadcast();
    }

    /// <summary>Coalesces bursts (a playing track ticks, a tab title changes) into one update per window.</summary>
    private void ScheduleBroadcast()
    {
        lock (_broadcastGate)
        {
            if (_broadcastScheduled)
            {
                return;
            }

            _broadcastScheduled = true;
        }

        _ = Task.Delay(BroadcastDebounce).ContinueWith(async _ =>
        {
            lock (_broadcastGate)
            {
                _broadcastScheduled = false;
            }

            DevicesChanged?.Invoke();
            var devices = GetDevices();
            foreach (var recipient in devices.Where(device => device.Kind == DeviceKinds.Phone && device.Online))
            {
                var peers = devices
                    .Where(device => device.DeviceId != recipient.DeviceId)
                    .Select(device => new PeerInfo(device.DeviceId, device.Name, device.Kind, device.Online, device.Presence, device.Activities))
                    .ToArray();
                await _host.SendAsync(recipient.DeviceId, MessageTypes.Peers, new PeersPayload(peers));
            }
        }, TaskScheduler.Default);
    }

    private void Track(HandoffEvent handoff, HandoffIntent? intent = null)
    {
        var existing = History.Find(handoff.RequestId);
        if (existing?.Event.Status is not null || existing?.Event.Unconfirmed == true) return;
        var started = _pending.TryGetValue(handoff.RequestId, out var old) ? old.StartedAt : DateTimeOffset.UtcNow;
        _pending[handoff.RequestId] = (handoff, started);
        if (History.Observe(handoff, intent)) HandoffUpdated?.Invoke(handoff);
        foreach (var stale in _pending.OrderByDescending(pair => pair.Value.StartedAt).Skip(40).ToArray())
        {
            _pending.TryRemove(stale.Key, out _);
            _speculative.TryRemove(stale.Key, out _);
        }

    }

    public void FailRequest(string requestId, string detail) => Finish(requestId, HandoffStatus.Failed, detail);
    private void Finish(string requestId, HandoffStatus status, string? detail)
    {
        _speculative.TryRemove(requestId, out _);
        var value = _pending.TryRemove(requestId, out var pending) ? pending.Event : History.Find(requestId)?.Event;
        if (value is not null && History.Find(requestId) is not null) Report(value with { Status = status, Detail = detail, Unconfirmed = false });
    }

    private void Report(HandoffEvent handoff)
    {
        if (History.Observe(handoff)) HandoffUpdated?.Invoke(handoff);
    }

    private async Task CompleteSpeculativeAsync(HandoffDeliverPayload deliver, Activity guessed, Task<OpenResult> opening)
    {
        var result = await opening;
        if (result.Status == HandoffStatus.Failed)
        {
            await OpenAndReportAsync(deliver);
            return;
        }
        var corrected = await ReconcileAsync(deliver.RequestId, guessed, deliver.Activity);
        var detail = result.Status != HandoffStatus.Opened || deliver.Activity.Playback is null ? result.Detail ?? "Opened on this PC." :
            corrected ? "Opened; playback position reconciled." : "Opened; playback position could not be confirmed.";
        Finish(deliver.RequestId, result.Status, detail);
        await _host.SendAsync(deliver.SourceDeviceId, MessageTypes.HandoffResult,
            new HandoffResultPayload(deliver.RequestId, deliver.SourceDeviceId, LocalDeviceId, result.Status, detail));
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");
}

/// <summary>An opener that can move something it already opened to the right position.</summary>
public interface IPositionReconciler
{
    Task<bool> ReconcileAsync(Activity activity, long positionMs, CancellationToken cancellationToken);
}

public static class ActivityRanking
{
    /// <summary>
    /// Most relevant first: what is playing now, then recently paused media, then pages and
    /// windows. Within a tier, what the user is watching comes first (see <see cref="Attention"/>),
    /// then the newest. Duplicates by id keep the one that knows its playback position, else the newest;
    /// a window already offered as the media playing in it isn't offered again as a plain window.
    /// </summary>
    public static IReadOnlyList<Activity> Rank(IEnumerable<Activity> activities, DateTimeOffset now)
    {
        var all = activities.ToArray();
        var mediaWindows = all.Where(activity => activity.Kind != ActivityKind.WindowStream && activity.Window is not null)
            .Select(activity => activity.Window!.WindowToken).ToHashSet(StringComparer.Ordinal);
        return all
            .Where(activity => activity.Kind != ActivityKind.WindowStream || activity.Window is null || !mediaWindows.Contains(activity.Window.WindowToken))
            .GroupBy(activity => activity.Id)
            .Select(group => group.OrderByDescending(activity => activity.Playback is not null).ThenByDescending(activity => activity.UpdatedAt).First())
            .OrderByDescending(activity => Tier(activity, now))
            .ThenByDescending(Attention)
            .ThenByDescending(activity => activity.UpdatedAt)
            .ToArray();
    }

    /// <summary>
    /// Sound counts most (a muted autoplay video in a background tab is not what anyone watches),
    /// then being in the window last in front. Unknown counts as neither.
    /// </summary>
    private static int Attention(Activity activity) =>
        (activity.Audible switch { true => 2, false => -2, null => 0 }) + (activity.Focused == true ? 1 : 0);

    private static int Tier(Activity activity, DateTimeOffset now) => activity switch
    {
        { Playback.Playing: true } => 4,
        { Playback: not null } when now - activity.UpdatedAt < TimeSpan.FromMinutes(30) => 3,
        { Kind: ActivityKind.WebPage or ActivityKind.WebMedia } => 2,
        { Playback: not null } => 1,
        _ => 0
    };
}
