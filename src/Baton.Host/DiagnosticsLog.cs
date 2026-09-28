namespace Baton.Host;

public enum DiagnosticsCategory
{
    Transport,
    Pairing,
    Handoff,
    Stream,
    Media
}

public enum DiagnosticsSeverity
{
    Debug,
    Info,
    Warning,
    Error
}

public sealed record DiagnosticsEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    DiagnosticsCategory Category,
    DiagnosticsSeverity Severity,
    string? DeviceId,
    string Event,
    string? Detail);

/// <summary>
/// Bounded in-memory event log. It exists so "the link never dropped" is provable rather than
/// merely felt: every transport attempt, close reason and reconnect lands here and is surfaced in
/// the desktop diagnostics view alongside the counters the phone reports.
/// </summary>
public sealed class DiagnosticsLog
{
    public const int DefaultCapacity = 2000;

    private readonly object _gate = new();
    private readonly DiagnosticsEntry[] _entries;
    private long _nextSequence;
    private int _count;
    private int _head;

    private readonly Dictionary<string, int> _reconnectReasons = new(StringComparer.Ordinal);
    private DateTimeOffset? _connectedSince;

    public DiagnosticsLog(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _entries = new DiagnosticsEntry[capacity];
    }

    public int Capacity => _entries.Length;

    public TimeSpan? ConnectedFor
    {
        get
        {
            lock (_gate)
            {
                return _connectedSince is { } since ? DateTimeOffset.UtcNow - since : null;
            }
        }
    }

    public void MarkConnected()
    {
        lock (_gate)
        {
            _connectedSince ??= DateTimeOffset.UtcNow;
        }
    }

    public void MarkDisconnected(string reason)
    {
        lock (_gate)
        {
            _connectedSince = null;
            var key = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
            _reconnectReasons[key] = _reconnectReasons.GetValueOrDefault(key) + 1;
        }
    }

    /// <summary>Histogram of why the link went down, which is the artifact the stability gate wants.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> ReconnectReasons()
    {
        lock (_gate)
        {
            return _reconnectReasons
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public void Record(
        DiagnosticsCategory category,
        string eventName,
        string? detail = null,
        string? deviceId = null,
        DiagnosticsSeverity severity = DiagnosticsSeverity.Info)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        lock (_gate)
        {
            var entry = new DiagnosticsEntry(
                _nextSequence++,
                DateTimeOffset.UtcNow,
                category,
                severity,
                string.IsNullOrWhiteSpace(deviceId) ? null : deviceId,
                eventName,
                string.IsNullOrWhiteSpace(detail) ? null : Truncate(detail));

            _entries[_head] = entry;
            _head = (_head + 1) % _entries.Length;
            if (_count < _entries.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>Most recent entries last, so the view reads like a tail.</summary>
    public IReadOnlyList<DiagnosticsEntry> Snapshot(int maxEntries = DefaultCapacity, DiagnosticsCategory? category = null)
    {
        if (maxEntries <= 0)
        {
            return Array.Empty<DiagnosticsEntry>();
        }

        lock (_gate)
        {
            if (_count == 0)
            {
                return Array.Empty<DiagnosticsEntry>();
            }

            var ordered = new List<DiagnosticsEntry>(_count);
            var start = (_head - _count + _entries.Length) % _entries.Length;
            for (var offset = 0; offset < _count; offset++)
            {
                var entry = _entries[(start + offset) % _entries.Length];
                if (category is null || entry.Category == category)
                {
                    ordered.Add(entry);
                }
            }

            if (ordered.Count <= maxEntries)
            {
                return ordered;
            }

            return ordered.GetRange(ordered.Count - maxEntries, maxEntries);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_entries);
            _count = 0;
            _head = 0;
            _reconnectReasons.Clear();
        }
    }

    private static string Truncate(string detail) =>
        detail.Length <= 512 ? detail : string.Concat(detail.AsSpan(0, 509), "...");
}
