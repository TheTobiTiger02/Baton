namespace Baton.Protocol;

// ---- Discovery and pairing ----

/// <summary>UDP discovery reply. The fingerprint lets the phone pin the PC before the first TLS handshake.</summary>
public sealed record DiscoveryResponse(
    string HostId,
    string PcName,
    int ProtocolVersion,
    int WssPort,
    string CertificateFingerprint,
    IReadOnlyList<HostEndpoint> Endpoints);

public sealed record HostEndpoint(string Host, int Port, string Kind, int Preference);

public sealed record HostEndpointsPayload(string HostId, IReadOnlyList<HostEndpoint> Endpoints);

public sealed record SessionChallengePayload(
    string ChallengeId,
    string Nonce,
    string HostId,
    string PcName,
    DateTimeOffset ExpiresAt);

/// <summary>Proof = hex(HMAC-SHA256(trustKey, "challengeId:nonce:deviceId:hostId")).</summary>
public sealed record SessionAuthenticatePayload(string ChallengeId, string DeviceId, string Proof);

public sealed record PairHelloPayload(
    string PairingCode,
    string DeviceId,
    string DisplayName,
    string Model,
    string? HostId = null);

public sealed record PairConfirmPayload(
    bool Accepted,
    string DeviceId,
    string? TrustKey,
    string? Reason,
    string? ErrorCode,
    string HostId,
    string PcName,
    string CertificateFingerprint);

public sealed record SessionReadyPayload(
    string DeviceId,
    string SessionId,
    string HostId,
    string PcName,
    int HeartbeatSeconds = 20,
    int StaleAfterSeconds = 60);

public sealed record HeartbeatPayload(DateTimeOffset SentAt);

/// <summary>
/// About a phone. <see cref="ScreenWidth"/> x <see cref="ScreenHeight"/> is the screen in pixels in
/// its natural (portrait) orientation; window streams are encoded to fit it exactly.
/// </summary>
public sealed record DeviceInfoPayload(string DisplayName, string Model, int? BatteryLevel = null, int? ScreenWidth = null, int? ScreenHeight = null);

public sealed record DeviceForgetPayload(string? Reason = null);

public sealed record ErrorPayload(string Code, string Message, string? CorrelationId = null);

// ---- Activities and handoff ----

public enum ActivityKind
{
    /// <summary>A web page without playing media: continue at the same URL.</summary>
    WebPage,

    /// <summary>Audio or video inside a web page: URL plus playback position.</summary>
    WebMedia,

    /// <summary>A native media app's session (Spotify, YouTube app, Netflix app...).</summary>
    AppMedia,

    /// <summary>A local media file played by some player.</summary>
    LocalMedia,

    /// <summary>Anything else: continue by streaming the window live.</summary>
    WindowStream
}

public enum PresenceState
{
    Active,
    Idle,
    Locked
}

/// <summary>The app an activity runs in. <see cref="Id"/> is a Windows AUMID or process name, or an Android package.</summary>
public sealed record ActivityApp(string Name, string Id);

/// <summary>
/// What the content is, independent of the app showing it: <see cref="Provider"/> is youtube,
/// netflix, spotify, twitch... with the provider's own <see cref="Id"/> when known, otherwise a
/// search <see cref="Query"/> that finds it.
/// </summary>
public sealed record ActivityContent(string Provider, string? Id = null, string? Query = null);

/// <summary>A file the source device can serve. <see cref="FileId"/> is opaque to everyone but the source.</summary>
public sealed record ActivityFile(string FileId, string Name, long Size, string Mime);

/// <summary>A playback snapshot. While playing, the position advances from <see cref="CapturedAt"/>.</summary>
public sealed record Playback(
    long PositionMs,
    long DurationMs,
    bool Playing,
    double Rate,
    DateTimeOffset CapturedAt)
{
    /// <summary>Longest believable duration or position. Live streams and some apps report far more.</summary>
    public static readonly long MaxPlausibleMs = (long)TimeSpan.FromDays(7).TotalMilliseconds;

    public long PositionAt(DateTimeOffset now)
    {
        var start = Math.Clamp(PositionMs, 0, MaxPlausibleMs);
        if (!Playing)
        {
            return start;
        }

        var rate = double.IsFinite(Rate) && Rate > 0 ? Rate : 1;
        var elapsed = (long)Math.Clamp((now - CapturedAt).TotalMilliseconds * rate, 0, MaxPlausibleMs);
        var position = start + elapsed;
        return DurationMs > 0 ? Math.Min(position, DurationMs) : position;
    }

    /// <summary>
    /// This snapshot with values no player could mean replaced: a duration beyond
    /// <see cref="MaxPlausibleMs"/> becomes unknown, and the position is kept in range.
    /// </summary>
    public Playback Normalized() => this with
    {
        DurationMs = DurationMs > 0 && DurationMs <= MaxPlausibleMs ? DurationMs : 0,
        PositionMs = Math.Clamp(PositionMs, 0, MaxPlausibleMs),
    };
}

public sealed record ActivityWindow(string WindowToken, string ProcessName);

public sealed record Activity(
    string Id,
    string DeviceId,
    ActivityKind Kind,
    string Title,
    ActivityApp App,
    DateTimeOffset UpdatedAt,
    string? Subtitle = null,
    string? ArtworkJpegBase64 = null,
    string? Url = null,
    ActivityContent? Content = null,
    ActivityFile? File = null,
    Playback? Playback = null,
    ActivityWindow? Window = null,
    double? Volume = null)
{
    /// <summary>This activity as a peer or a media session reported it, with its playback made safe to compute with.</summary>
    public Activity Normalized() => Playback is { } playback ? this with { Playback = playback.Normalized() } : this;
}

/// <summary>A device's continuable activities, most relevant first.</summary>
public sealed record ActivityListPayload(IReadOnlyList<Activity> Activities, PresenceState Presence);

public sealed record PeerInfo(
    string DeviceId,
    string Name,
    string Kind,
    bool Online,
    PresenceState Presence,
    IReadOnlyList<Activity> Activities);

/// <summary>PC to phones: every other device in the group, including the PC itself.</summary>
public sealed record PeersPayload(IReadOnlyList<PeerInfo> Peers);

/// <summary>
/// Asks <see cref="SourceDeviceId"/> to hand an activity (its current one when
/// <see cref="ActivityId"/> is null) to <see cref="TargetDeviceId"/>.
/// </summary>
public sealed record HandoffPullPayload(
    string RequestId,
    string SourceDeviceId,
    string TargetDeviceId,
    string? ActivityId = null,
    bool Speculative = false,
    string Mode = HandoffModes.Auto,
    HandoffChoice? Choice = null);

/// <summary>
/// Tells <see cref="TargetDeviceId"/> to continue <see cref="Activity"/>. <see cref="Stream"/> is
/// set when the activity continues as a live stream of the source's window.
/// </summary>
public sealed record HandoffDeliverPayload(
    string RequestId,
    string SourceDeviceId,
    string TargetDeviceId,
    Activity Activity,
    StreamOffer? Stream = null,
    HandoffChoice? Choice = null);

/// <summary>
/// A live stream that continues the activity. Frames travel on the source device's media channel
/// (see <see cref="MediaChannelPayload"/>): <see cref="StreamKinds.Window"/> is a PC window shown on
/// a phone, <see cref="StreamKinds.Phone"/> is a phone's screen shown on the PC.
/// </summary>
public sealed record StreamOffer(string SessionId, int Width, int Height, string Title, string Kind = StreamKinds.Window);

public enum HandoffStatus
{
    /// <summary>Continued the way the activity asked for.</summary>
    Opened,

    /// <summary>Continued, but in a lesser way (search page instead of the exact item, no position...).</summary>
    Fallback,

    Failed
}

/// <summary>
/// How a handoff ended. <see cref="OpenedMs"/> and <see cref="FirstFrameMs"/> are measured by the
/// target from the moment the user asked, for the timeline.
/// </summary>
public sealed record HandoffResultPayload(
    string RequestId,
    string SourceDeviceId,
    string TargetDeviceId,
    HandoffStatus Status,
    string? Detail = null,
    long? OpenedMs = null,
    long? FirstFrameMs = null);

public static class HandoffModes
{
    /// <summary>The best way: the other device's own app when there is one, a stream otherwise.</summary>
    public const string Auto = "auto";

    /// <summary>Always a live stream of the window or screen.</summary>
    public const string Stream = "stream";
}

public static class StreamKinds
{
    public const string Window = "window";
    public const string Phone = "phone";
}

/// <summary>
/// The persistent media channel: one pinned TLS socket per phone to the stream port, opened once
/// per session so no stream ever waits for a handshake. The phone presents <see cref="Ticket"/>
/// (base64) with <see cref="StreamId"/>; records carry their channel in the header.
/// </summary>
public sealed record MediaChannelPayload(string Ticket, ulong StreamId, int Port);

/// <summary>Session-level stream commands: stop, return (continue on the source), fit, unfit.</summary>
public sealed record StreamControlPayload(string SessionId, string Action, long? ElapsedMs = null);

public static class StreamActions
{
    public const string Stop = "stop";
    public const string Return = "return";
    public const string Fit = "fit";
    public const string Unfit = "unfit";

    /// <summary>From the viewer: the first frame is on screen, <see cref="StreamControlPayload.ElapsedMs"/> after the user asked.</summary>
    public const string FirstFrame = "first-frame";
}

/// <summary>
/// Remote control of an activity on <see cref="OwnerDeviceId"/>. <see cref="Action"/> is play,
/// pause, toggle, seek (with <see cref="PositionMs"/>), skip (with <see cref="PositionMs"/> as a
/// signed offset), next, previous or volume (with <see cref="Volume"/> 0..1).
/// </summary>
public sealed record MediaCommandPayload(string OwnerDeviceId, string ActivityId, string Action, long? PositionMs = null, double? Volume = null);

public static class DeviceKinds
{
    public const string Pc = "pc";
    public const string Phone = "phone";
}

/// <summary>
/// How an activity continues on the other device, as the user chose it (or Baton's best guess):
/// <see cref="ChoiceKinds.Default"/> the usual way (the content's own app, at the same second),
/// <see cref="ChoiceKinds.App"/> a specific app there (<see cref="AppId"/>: an AUMID, launch path or
/// browser on Windows, a package on Android), <see cref="ChoiceKinds.Web"/> a website
/// (<see cref="Url"/>), or <see cref="ChoiceKinds.Stream"/> the source's screen or window, live.
/// <see cref="Remember"/> asks the PC to use it for this app from now on.
/// </summary>
public sealed record HandoffChoice(string Kind, string Label, string? AppId = null, string? Url = null, bool Remember = false);

public static class ChoiceKinds
{
    public const string Default = "default";
    public const string App = "app";
    public const string Web = "web";
    public const string Stream = "stream";
}

/// <summary>Asks the PC how <see cref="Activity"/> could continue on <see cref="TargetDeviceId"/>.</summary>
public sealed record HandoffOptionsRequest(string RequestId, Activity Activity, string TargetDeviceId);

/// <summary>The ways it can continue, best first, and the one the user chose before, if any.</summary>
public sealed record HandoffOptionsPayload(string RequestId, IReadOnlyList<HandoffChoice> Options, HandoffChoice? Remembered = null);

/// <summary>An app installed on a device, as a launcher shows it.</summary>
public sealed record InstalledApp(string Id, string Name);

/// <summary>Phone to PC: every app that can be opened on the phone.</summary>
public sealed record AppCatalogPayload(IReadOnlyList<InstalledApp> Apps);

/// <summary>
/// A remembered choice: <see cref="Key"/> is <c>{platform}:{appId}-&gt;{platform}</c> (see
/// <see cref="ChoiceKeys"/>); <see cref="SourceName"/> is the app's name for showing the list.
/// </summary>
public sealed record AppPreference(string Key, string SourceName, HandoffChoice Choice);

/// <summary>PC to phones: all remembered choices, so phones skip asking. Phone to PC: remove one.</summary>
public sealed record AppPreferencesPayload(IReadOnlyList<AppPreference> Preferences);

public sealed record AppPreferenceRemovePayload(string Key);

public static class Platforms
{
    public const string Windows = "windows";
    public const string Android = "android";

    public static string Of(string deviceKind) => deviceKind == DeviceKinds.Pc ? Windows : Android;
}

public static class ChoiceKeys
{
    /// <summary>The remembered-choice key for an app on one platform continuing on another. Same on the phone.</summary>
    public static string For(string sourcePlatform, string appId, string targetPlatform) =>
        $"{sourcePlatform}:{appId.ToLowerInvariant()}->{targetPlatform}";

    public static string For(string sourcePlatform, Activity activity, string targetPlatform) =>
        For(sourcePlatform, Subject(activity), targetPlatform);

    /// <summary>
    /// What a choice is remembered for: the site for anything in a browser (<c>site:youtube</c>,
    /// <c>site:x.com</c>; a browser shows everything, so "Zen" says nothing about where a video
    /// should go), the app otherwise. Same on the phone.
    /// </summary>
    public static string Subject(Activity activity) =>
        activity.Kind is ActivityKind.WebMedia or ActivityKind.WebPage ? $"site:{Site(activity)}" : activity.App.Id;

    public static string Site(Activity activity)
    {
        if (activity.Content?.Provider is { } provider && provider is not ("web" or "unknown" or ""))
        {
            return provider;
        }

        return Uri.TryCreate(activity.Url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host).ToLowerInvariant()
            : "web";
    }
}
