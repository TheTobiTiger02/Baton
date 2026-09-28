using Baton.Protocol;

namespace Baton.Host.Handoff;

/// <summary>
/// Which of another device's activities is worth offering when the user switches devices, and
/// not offering the same one over and over. The phone applies the same rule (Suggestions.kt).
/// </summary>
public sealed class HandoffSuggestions
{
    /// <summary>Something that stopped longer ago than this is not what the user was just doing.</summary>
    public static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(5);

    /// <summary>An activity offered once is not offered again for this long.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, DateTimeOffset> _offered = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// The activity to offer from a device, or null: playing media first, else the most recent one
    /// touched within <see cref="RecentFor"/>. Nothing while that device is in use and not playing,
    /// since the user is probably still on it.
    /// </summary>
    public static Activity? Pick(IReadOnlyList<Activity> activities, PresenceState presence, DateTimeOffset now)
    {
        var playing = activities.FirstOrDefault(activity => activity.Playback?.Playing == true);
        if (playing is not null)
        {
            return playing;
        }

        if (presence == PresenceState.Active)
        {
            return null;
        }

        return activities
            .Where(activity => now - activity.UpdatedAt < RecentFor)
            .MaxBy(activity => activity.UpdatedAt);
    }

    /// <summary>Records <paramref name="activity"/> as offered; false when it already was, within <see cref="RepeatAfter"/>.</summary>
    public bool TryOffer(string deviceId, Activity activity, DateTimeOffset now)
    {
        var key = $"{deviceId}\n{activity.Id}\n{activity.Title}";
        lock (_lock)
        {
            foreach (var stale in _offered.Where(pair => now - pair.Value >= RepeatAfter).Select(pair => pair.Key).ToList())
            {
                _offered.Remove(stale);
            }

            return _offered.TryAdd(key, now);
        }
    }
}
