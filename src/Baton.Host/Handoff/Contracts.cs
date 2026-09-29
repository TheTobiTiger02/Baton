using Baton.Protocol;

namespace Baton.Host.Handoff;

/// <summary>
/// Something on this PC that knows about continuable activities: media sessions, the browser
/// extension, local players, windows.
/// </summary>
public interface IActivitySource
{
    /// <summary>Raised whenever <see cref="Current"/> may have changed. Any thread.</summary>
    event Action? Changed;

    IReadOnlyList<Activity> Current { get; }

    /// <summary>
    /// Returns a fresh copy of the activity (exact position right now), pausing it on this PC when
    /// <paramref name="pause"/> is set. Null when it no longer exists.
    /// </summary>
    Task<Activity?> TakeAsync(string activityId, bool pause, CancellationToken cancellationToken);
}

/// <summary>Continues an activity that another device handed to this PC.</summary>
public interface IActivityOpener
{
    /// <summary>Returns null when this opener cannot handle the activity, so the next one is tried.</summary>
    Task<OpenResult?> TryOpenAsync(Activity activity, CancellationToken cancellationToken);
}

public sealed record OpenResult(HandoffStatus Status, string? Detail = null)
{
    public static OpenResult Opened(string? detail = null) => new(HandoffStatus.Opened, detail);
    public static OpenResult Fallback(string detail) => new(HandoffStatus.Fallback, detail);
    public static OpenResult Failed(string detail) => new(HandoffStatus.Failed, detail);
}

/// <summary>One device as the UI shows it: the PC itself or a phone.</summary>
public sealed record DeviceView(
    string DeviceId,
    string Name,
    string Kind,
    bool Online,
    PresenceState Presence,
    IReadOnlyList<Activity> Activities);

/// <summary>A handoff this PC started or received, for progress toasts.</summary>
public sealed record HandoffEvent(
    string RequestId,
    string SourceDeviceId,
    string TargetDeviceId,
    string Title,
    HandoffStatus? Status,
    string? Detail,
    bool Unconfirmed = false);

/// <summary>A source whose activities can be played, paused, seeked and turned up or down remotely.</summary>
public interface IActivityControl
{
    /// <summary>Returns false when the activity is not this source's or the command is not possible.</summary>
    Task<bool> CommandAsync(Activity activity, MediaCommandPayload command, CancellationToken cancellationToken);
}

public static class MediaActions
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Toggle = "toggle";
    public const string Seek = "seek";
    public const string Skip = "skip";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string Volume = "volume";
}
