using System.Collections.Concurrent;
using Baton.Host.Media;
using Baton.Protocol;

namespace Baton.Host.Handoff;

/// <summary>
/// Works out ahead of time what a handoff would otherwise look up when the user asks: the exact
/// YouTube video behind a title-only media session (the YouTube app, a browser without the
/// extension). Done in the background as soon as an activity appears, so "Continue here" can open
/// the video immediately.
/// </summary>
public sealed class ActivityEnricher(YouTubeResolver youtube, DiagnosticsLog diagnostics)
{
    private readonly ConcurrentDictionary<string, string?> _resolved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    /// <summary>A lookup finished; activities enriched again now carry more.</summary>
    public event Action? Changed;

    public IReadOnlyList<Activity> Enrich(IReadOnlyList<Activity> activities)
    {
        var result = new Activity[activities.Count];
        for (var index = 0; index < activities.Count; index++)
        {
            result[index] = Enrich(activities[index]);
        }

        return result;
    }

    public Activity Enrich(Activity activity)
    {
        if (!NeedsVideoId(activity))
        {
            return activity;
        }

        var key = Key(activity);
        if (_resolved.TryGetValue(key, out var id))
        {
            return id is null ? activity : activity with
            {
                Content = new ActivityContent(activity.Content?.Provider is "youtubemusic" ? "youtubemusic" : "youtube", id, activity.Content?.Query)
            };
        }

        if (_inFlight.TryAdd(key, 0))
        {
            _ = ResolveAsync(key, activity);
        }

        return activity;
    }

    /// <summary>
    /// Title-only video sessions that may be YouTube. Unknown apps only qualify when the length is
    /// known, since the length is what keeps a film from matching its trailer.
    /// </summary>
    public static bool NeedsVideoId(Activity activity) =>
        activity.Url is null
        && activity.Content is { Id: null } content
        && activity.Playback is { } playback
        && (content.Provider is "youtube" or "youtubemusic"
            || (content.Provider is "web" or "unknown" && playback.DurationMs > 0));

    private static string Key(Activity activity) =>
        $"{activity.Title}|{activity.Subtitle}|{(activity.Playback?.DurationMs ?? 0) / 1000}";

    private async Task ResolveAsync(string key, Activity activity)
    {
        try
        {
            var id = await youtube.FindVideoIdAsync(activity.Title, activity.Subtitle, activity.Playback?.DurationMs ?? 0, CancellationToken.None);
            _resolved[key] = id;
            diagnostics.Record(DiagnosticsCategory.Handoff, "enrich", $"'{activity.Title}' -> {id ?? "no match"}");
            if (id is not null)
            {
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // Not retried: a lookup that fails now would most likely fail again right away.
            _resolved[key] = null;
            diagnostics.Record(DiagnosticsCategory.Handoff, "enrich.failed", ex.Message, severity: DiagnosticsSeverity.Debug);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }
}
