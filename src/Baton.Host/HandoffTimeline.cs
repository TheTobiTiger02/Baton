using System.Collections.Concurrent;
using System.Diagnostics;

namespace Baton.Host;

/// <summary>
/// How long each handoff took, stage by stage, so "instant" is measured rather than felt. Each
/// request is timed from its first mark; stages land in the diagnostics log as
/// <c>timeline +123 ms stage</c> and are kept for the <c>timings</c> view.
/// </summary>
public sealed class HandoffTimeline(DiagnosticsLog diagnostics)
{
    private const int Keep = 40;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Mark(string requestId, string stage, long? reportedMs = null)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }

        var entry = _entries.GetOrAdd(requestId, _ => new Entry(Stopwatch.StartNew(), DateTimeOffset.UtcNow));
        var elapsed = reportedMs ?? entry.Clock.ElapsedMilliseconds;
        lock (entry.Stages)
        {
            entry.Stages.Add((stage, elapsed));
        }

        diagnostics.Record(DiagnosticsCategory.Handoff, "timeline", $"{requestId[..Math.Min(8, requestId.Length)]} +{elapsed} ms {stage}");
        if (_entries.Count > Keep)
        {
            foreach (var old in _entries.OrderBy(pair => pair.Value.StartedAt).Take(_entries.Count - Keep))
            {
                _entries.TryRemove(old.Key, out _);
            }
        }
    }

    /// <summary>The most recent handoffs, newest first, each with its stages in order.</summary>
    public IReadOnlyList<(string RequestId, DateTimeOffset StartedAt, IReadOnlyList<(string Stage, long Ms)> Stages)> Recent(int count = 10) =>
        _entries
            .OrderByDescending(pair => pair.Value.StartedAt)
            .Take(count)
            .Select(pair =>
            {
                lock (pair.Value.Stages)
                {
                    return (pair.Key, pair.Value.StartedAt, (IReadOnlyList<(string, long)>)pair.Value.Stages.ToArray());
                }
            })
            .ToArray();

    private sealed record Entry(Stopwatch Clock, DateTimeOffset StartedAt)
    {
        public List<(string Stage, long Ms)> Stages { get; } = [];
    }
}
