using System.Collections.Concurrent;
using Baton.Host.Handoff;
using Baton.Media;
using Baton.Protocol;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Baton.Host.Media;

/// <summary>
/// Every app that publishes a Windows media session (the thing behind the volume flyout's media
/// controls): Spotify, the Netflix app, browsers playing video, Media Player. Gives title, artist,
/// artwork, position and pause/seek control without any per-app integration.
/// </summary>
public sealed class MediaSessionSource : IActivitySource, IActivityControl, IDisposable
{
    private static readonly TimeSpan ResumeSettleDelay = TimeSpan.FromMilliseconds(600);
    private const long ResumeToleranceMs = 3_000;

    // Activity ids are not always the session's (a local file's is the file's): which session is whose.
    private readonly ConcurrentDictionary<string, string> _appIdByActivity = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, double? Volume)> _volumeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Activity> _activities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _artworkCache = new(StringComparer.Ordinal);
    private readonly DiagnosticsLog _diagnostics;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = [];

    private readonly LocalFiles _files;
    private readonly ConcurrentDictionary<string, (DateTime At, WindowInfo? Window)> _windowCache = new(StringComparer.Ordinal);

    public MediaSessionSource(DiagnosticsLog diagnostics, LocalFiles files)
    {
        _diagnostics = diagnostics;
        _files = files;
    }

    public event Action? Changed;

    /// <summary>
    /// Browsers also publish sessions. When the extension reports the same tab with its URL, it is
    /// the better source; it registers here which browser sessions it covers.
    /// </summary>
    public Func<Activity, bool>? IsCoveredElsewhere { get; set; }

    public IReadOnlyList<Activity> Current =>
        _activities.Values.Where(activity => IsCoveredElsewhere?.Invoke(activity) != true).ToArray();

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => _ = RefreshAsync();
        await RefreshAsync();
    }

    public async Task<Activity?> TakeAsync(string activityId, bool pause, CancellationToken cancellationToken)
    {
        var session = FindSession(activityId);
        if (session is null)
        {
            return null;
        }

        if (pause)
        {
            await session.TryPauseAsync();
        }

        return await ReadAsync(session);
    }

    /// <summary>
    /// Waits for a session that looks like <paramref name="matches"/> to appear, then moves it to
    /// <paramref name="positionMs"/> if it is more than <paramref name="toleranceMs"/> away and
    /// makes sure it plays. This is what makes an app resume at the right second even when the link
    /// that opened it could not carry a timestamp.
    /// </summary>
    public async Task<bool> ReconcileAsync(
        Func<Activity, bool> matches,
        long positionMs,
        bool play,
        TimeSpan timeout,
        long toleranceMs = 3_000)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await RefreshAsync();
            foreach (var session in Sessions())
            {
                var activity = await ReadAsync(session);
                if (activity?.Playback is not { } playback || !matches(activity))
                {
                    continue;
                }

                // The app must have loaded the content (a duration) before a seek can stick.
                if (playback.DurationMs <= 0 && positionMs > 0)
                {
                    continue;
                }

                if (positionMs > 0 && Math.Abs(playback.PositionAt(DateTimeOffset.UtcNow) - positionMs) > toleranceMs)
                {
                    await session.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(positionMs).Ticks);
                }

                if (play && !playback.Playing)
                {
                    await session.TryPlayAsync();
                }

                _diagnostics.Record(DiagnosticsCategory.Media, "reconciled", $"{activity.Title} -> {positionMs} ms");
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    public async Task<bool> CommandAsync(Activity activity, MediaCommandPayload command, CancellationToken cancellationToken)
    {
        var session = FindSession(activity.Id);
        if (session is null)
        {
            return false;
        }

        var ok = command.Action switch
        {
            MediaActions.Play => await session.TryPlayAsync(),
            MediaActions.Pause => await session.TryPauseAsync(),
            MediaActions.Toggle => await session.TryTogglePlayPauseAsync(),
            MediaActions.Next => await session.TrySkipNextAsync(),
            MediaActions.Previous => await session.TrySkipPreviousAsync(),
            MediaActions.Seek when command.PositionMs is { } position => await SeekAsync(session, position),
            MediaActions.Skip when command.PositionMs is { } offset =>
                await SeekAsync(session, (long)session.GetTimelineProperties().Position.TotalMilliseconds
                    + (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        ? (long)(DateTimeOffset.UtcNow - session.GetTimelineProperties().LastUpdatedTime).TotalMilliseconds
                        : 0)
                    + offset),
            MediaActions.Volume when command.Volume is { } volume && activity.Window?.ProcessName is { } process => SetVolume(process, volume),
            _ => false
        };
        _diagnostics.Record(DiagnosticsCategory.Media, "remote.command", $"{command.Action} {command.PositionMs}{command.Volume} on {activity.Title}: {ok}");
        _ = UpdateAsync(session);
        return ok;
    }

    private static async Task<bool> SeekAsync(GlobalSystemMediaTransportControlsSession session, long positionMs) =>
        await session.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(Math.Max(0, positionMs)).Ticks);

    private bool SetVolume(string processName, double volume)
    {
        var ok = AppVolume.Set(processName, volume);
        _volumeCache[processName] = (DateTime.UtcNow, ok ? volume : null);
        return ok;
    }

    /// <summary>Reading the mixer walks every audio session, so each app's level is kept briefly.</summary>
    private double? VolumeOf(string processName)
    {
        if (_volumeCache.TryGetValue(processName, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(3))
        {
            return cached.Volume;
        }

        var volume = AppVolume.Get(processName);
        _volumeCache[processName] = (DateTime.UtcNow, volume);
        return volume;
    }

    /// <summary>
    /// Continues something coming back to this PC in the app or tab that still has it (paused
    /// when it left): seeks it, plays it and brings its window forward, instead of opening it again.
    /// </summary>
    public async Task<bool> TryResumeAsync(Activity activity, long positionMs)
    {
        foreach (var session in Sessions())
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties is null || !MediaOpener.TitlesMatch(properties.Title, activity.Title))
            {
                continue;
            }

            await session.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(Math.Max(0, positionMs)).Ticks);
            await session.TryPlayAsync();
            if (WindowFor(session.SourceAppUserModelId) is { } window)
            {
                DesktopWindows.BringToFront(window.Handle);
            }

            // Some players ignore seeks (Firefox-based browsers): only a position that actually
            // moved counts, otherwise the caller opens it afresh at the right second.
            await Task.Delay(ResumeSettleDelay);
            var timeline = session.GetTimelineProperties();
            var now = timeline is null ? (long?)null : (long)timeline.Position.TotalMilliseconds;
            var moved = now is null || Math.Abs(now.Value - positionMs) <= ResumeToleranceMs;
            _diagnostics.Record(DiagnosticsCategory.Handoff, moved ? "resume.session" : "resume.session.ignored-seek",
                $"{session.SourceAppUserModelId}: {properties.Title} at {now} ms, wanted {positionMs} ms");
            return moved;
        }

        return false;
    }

    /// <summary>
    /// Pauses every playing session showing <paramref name="title"/>, including ones hidden because a
    /// browser tab reports the same media: the fallback when pausing that tab did not take. True
    /// when one was paused.
    /// </summary>
    public async Task<bool> PauseMatchingAsync(string title)
    {
        var paused = false;
        foreach (var session in Sessions())
        {
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (!playing)
            {
                continue;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties is not null && MediaOpener.TitlesMatch(properties.Title ?? string.Empty, title))
            {
                paused |= await session.TryPauseAsync();
            }
        }

        return paused;
    }

    public void Dispose() => _refreshGate.Dispose();

    private IEnumerable<GlobalSystemMediaTransportControlsSession> Sessions() =>
        _manager?.GetSessions() ?? (IEnumerable<GlobalSystemMediaTransportControlsSession>)[];

    private GlobalSystemMediaTransportControlsSession? FindSession(string activityId)
    {
        _appIdByActivity.TryGetValue(activityId, out var appId);
        return Sessions().FirstOrDefault(session => IdFor(session) == activityId || session.SourceAppUserModelId == appId);
    }

    private static string IdFor(GlobalSystemMediaTransportControlsSession session) => $"media:{session.SourceAppUserModelId}";

    private async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            var sessions = Sessions().ToArray();
            foreach (var session in sessions.Where(session => !_subscribed.Contains(session)))
            {
                _subscribed.Add(session);
                session.MediaPropertiesChanged += (s, e) => _ = UpdateAsync(s);
                session.PlaybackInfoChanged += (s, e) => _ = UpdateAsync(s);
                session.TimelinePropertiesChanged += (s, e) => _ = UpdateAsync(s);
            }

            _subscribed.RemoveAll(session => !sessions.Contains(session));
            var live = sessions.Select(IdFor).ToHashSet();
            foreach (var gone in _activities.Keys.Where(id => !live.Contains(id)))
            {
                _activities.TryRemove(gone, out _);
            }

            foreach (var session in sessions)
            {
                await StoreAsync(session);
            }
        }
        catch (Exception ex)
        {
            _diagnostics.Record(DiagnosticsCategory.Media, "sessions.refresh.failed", ex.Message, severity: DiagnosticsSeverity.Warning);
        }
        finally
        {
            _refreshGate.Release();
        }

        Changed?.Invoke();
    }

    private async Task UpdateAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            if (await StoreAsync(session))
            {
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // Sessions vanish mid-read when an app closes; the next refresh drops them.
            _diagnostics.Record(DiagnosticsCategory.Media, "session.read.failed", ex.Message, severity: DiagnosticsSeverity.Debug);
        }
    }

    /// <summary>Stores the session's activity; true when anything a viewer would notice changed.</summary>
    private async Task<bool> StoreAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var activity = await ReadAsync(session);
        var id = IdFor(session);
        if (activity is null)
        {
            return _activities.TryRemove(id, out _);
        }

        _activities.TryGetValue(id, out var previous);

        // Playing since when, not "now" on every read: the newest thing started wins, not
        // whichever player last reported its position.
        if (previous is { Playback.Playing: true } && activity.Playback?.Playing == true)
        {
            activity = activity with { UpdatedAt = previous.UpdatedAt };
        }

        if (previous is not null && IsSame(previous, activity))
        {
            return false;
        }

        _activities[id] = activity;
        _appIdByActivity[activity.Id] = session.SourceAppUserModelId;
        return true;
    }

    private static bool IsSame(Activity a, Activity b) =>
        a.Title == b.Title
        && a.Subtitle == b.Subtitle
        && a.ArtworkJpegBase64 == b.ArtworkJpegBase64
        && a.Playback?.Playing == b.Playback?.Playing
        && a.Audible == b.Audible
        && a.Playback?.DurationMs == b.Playback?.DurationMs
        && Math.Abs((a.Volume ?? -1) - (b.Volume ?? -1)) < 0.01

        // Timeline events fire every second or so while playing; a position that merely advanced
        // as expected is not news, a seek is.
        && Math.Abs((a.Playback?.PositionAt(DateTimeOffset.UtcNow) ?? 0) - (b.Playback?.PositionAt(DateTimeOffset.UtcNow) ?? 0)) < 2_000;

    private async Task<Activity?> ReadAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var properties = await session.TryGetMediaPropertiesAsync();
        if (properties is null || string.IsNullOrWhiteSpace(properties.Title))
        {
            return null;
        }

        var playbackInfo = session.GetPlaybackInfo();
        var status = playbackInfo.PlaybackStatus;
        if (status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
        {
            return null;
        }

        var timeline = session.GetTimelineProperties();
        var playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var capturedAt = timeline.LastUpdatedTime.Year > 2000 ? timeline.LastUpdatedTime : DateTimeOffset.UtcNow;
        var playback = new Playback(
            (long)timeline.Position.TotalMilliseconds,
            (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds,
            playing,
            playbackInfo.PlaybackRate ?? 1.0,
            capturedAt);

        var appId = session.SourceAppUserModelId;
        var known = KnownApps.FromWindowsId(appId);

        // An app Baton doesn't know by its id may still be one it knows by its window (Opera's id
        // is an opaque hash), may be playing a local file the phone can stream, or can have its
        // window streamed. One that has none of these can't continue anywhere and is left out.
        var window = WindowFor(appId);
        known ??= window is null ? null : KnownApps.FromWindowsId(window.ProcessName);
        var appName = known?.DisplayName ?? (window is null ? KnownApps.DisplayName(appId) : KnownApps.DisplayName(window.ProcessName));
        var artist = string.IsNullOrWhiteSpace(properties.Artist) ? properties.AlbumArtist : properties.Artist;
        var query = string.Join(' ', new[] { properties.Title, artist }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var artwork = await ReadArtworkAsync(appId, properties);

        var file = known is null && window is not null ? _files.FindPlaying(window.ProcessId, properties.Title) : null;
        if (known is null && window is null)
        {
            return null;
        }

        if (window is not null && file is not null)
        {
            var shared = _files.Share(file, ProcessPath(window.ProcessId));
            return new Activity(
                $"file:{shared.FileId}",
                string.Empty,
                ActivityKind.LocalMedia,
                properties.Title,
                new ActivityApp(appName, appId),
                playing ? DateTimeOffset.UtcNow : capturedAt,
                Subtitle: shared.Name,
                ArtworkJpegBase64: artwork,
                Content: new ActivityContent("file"),
                File: shared,
                Playback: playback,
                Window: new ActivityWindow(DesktopWindows.Token(window.Handle), window.ProcessName),
                Volume: VolumeOf(window.ProcessName),
                Audible: AppVolume.Audible(window.ProcessName));
        }

        return new Activity(
            IdFor(session),
            string.Empty,
            known?.IsBrowser == true ? ActivityKind.WebMedia : ActivityKind.AppMedia,
            properties.Title,
            new ActivityApp(appName, appId),
            playing ? DateTimeOffset.UtcNow : capturedAt,
            Subtitle: string.IsNullOrWhiteSpace(artist) ? null : artist,
            ArtworkJpegBase64: artwork,
            Content: new ActivityContent(known switch
            {
                { IsBrowser: true } => "web",
                not null => known.Provider,
                _ => "unknown"
            }, Query: query),
            Playback: playback,
            Window: window is null ? null : new ActivityWindow(DesktopWindows.Token(window.Handle), window.ProcessName),
            Volume: window is null ? null : VolumeOf(window.ProcessName),
            Audible: window is null ? null : AppVolume.Audible(window.ProcessName));
    }

    /// <summary>Window lookups walk every top-level window, so each app's answer is kept briefly.</summary>
    private WindowInfo? WindowFor(string appId)
    {
        if (_windowCache.TryGetValue(appId, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(15)
            && (cached.Window is null || DesktopWindows.Exists(cached.Window.Handle)))
        {
            return cached.Window;
        }

        var window = DesktopWindows.MainWindowOf(appId);
        _windowCache[appId] = (DateTime.UtcNow, window);
        return window;
    }

    public static string? ProcessPath(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> ReadArtworkAsync(string appId, GlobalSystemMediaTransportControlsSessionMediaProperties properties)
    {
        if (properties.Thumbnail is null)
        {
            return null;
        }

        var key = $"{appId}|{properties.Title}|{properties.Artist}";
        if (_artworkCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            using var source = await properties.Thumbnail.OpenReadAsync();
            var jpeg = await Artwork.ToJpegBase64Async(source);
            if (_artworkCache.Count > 64)
            {
                _artworkCache.Clear();
            }

            _artworkCache[key] = jpeg;
            return jpeg;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class Artwork
{
    public const int MaxEdge = 256;

    /// <summary>Re-encodes any image stream as a JPEG no larger than <see cref="MaxEdge"/> on its long edge.</summary>
    public static async Task<string> ToJpegBase64Async(IRandomAccessStream source)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var pixels = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
                ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
                InterpolationMode = BitmapInterpolationMode.Fant
            },
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        using var jpegStream = new InMemoryRandomAccessStream();
        var jpeg = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, jpegStream);
        jpeg.SetSoftwareBitmap(pixels);
        await jpeg.FlushAsync();

        var bytes = new byte[jpegStream.Size];
        using var reader = new DataReader(jpegStream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)jpegStream.Size);
        reader.ReadBytes(bytes);
        return Convert.ToBase64String(bytes);
    }
}
