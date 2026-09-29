using Baton.Protocol;

namespace Baton.Host.Handoff;

public sealed record HandoffIntent(string SourceDeviceId, string TargetDeviceId, string? ActivityId,
    string Mode, HandoffChoice? Choice, Activity? Activity = null, bool Standalone = false)
{
    public bool Streaming => Choice?.Kind switch
    {
        ChoiceKinds.Stream => true,
        ChoiceKinds.App or ChoiceKinds.Web => false,
        _ => Mode == HandoffModes.Stream || Activity is { } activity && HandoffCoordinator.ContinuesAsStream(activity)
    };
}

public sealed record HandoffRecord(HandoffEvent Event, DateTimeOffset StartedAt, HandoffIntent? Intent)
{
    public bool Recoverable => Event.Status == HandoffStatus.Failed || Event.Unconfirmed;
    public string Outcome => Event.Unconfirmed ? "No confirmation received" : Event.Status switch
    {
        HandoffStatus.Opened => "Opened on destination",
        HandoffStatus.Fallback => "Fallback used",
        HandoffStatus.Failed => "Couldn't continue",
        _ => "In progress"
    };
}

/// <summary>Process-local, bounded handoff state. A timeout is uncertainty, not a failed open.</summary>
public sealed class HandoffHistory
{
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly HashSet<string> _cleared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandoffRecord> _records = new(StringComparer.Ordinal);
    public event Action? Changed;

    public IReadOnlyList<HandoffRecord> Recent
    {
        get { lock (_gate) return _records.Values.OrderByDescending(item => item.StartedAt).ToArray(); }
    }

    public HandoffRecord? Find(string id)
    {
        lock (_gate) return _records.GetValueOrDefault(id);
    }

    public bool Observe(HandoffEvent value, HandoffIntent? intent = null, DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(value.RequestId)) return false;
        lock (_gate)
        {
            if (_cleared.Contains(value.RequestId)) return false;
            var old = _records.GetValueOrDefault(value.RequestId);
            // Expiry accepts a late result; a streaming error can follow opening its viewer.
            if (old?.Event.Status is not null && !(old.Intent?.Streaming == true &&
                old.Event.Status != HandoffStatus.Failed && value.Status == HandoffStatus.Failed)) return false;
            if (old?.Event.Unconfirmed == true && value.Status is null) return false;
            var next = new HandoffRecord(value, old?.StartedAt ?? now ?? DateTimeOffset.UtcNow, intent ?? old?.Intent);
            if (next == old) return false;
            _records[value.RequestId] = next;
            foreach (var oldest in _records.Values.OrderByDescending(item => item.StartedAt).Skip(40).ToArray())
            {
                _records.Remove(oldest.Event.RequestId);
                _cleared.Add(oldest.Event.RequestId);
            }
            while (_cleared.Count > 80) _cleared.Remove(_cleared.First());
        }
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<HandoffEvent> Expire(DateTimeOffset now)
    {
        HandoffEvent[] expired;
        lock (_gate)
        {
            expired = _records.Values.Where(item => item.Event.Status is null && !item.Event.Unconfirmed &&
                now - item.StartedAt >= PendingLifetime).Select(item => item.Event with
                { Unconfirmed = true, Detail = "No confirmation received. The activity may already have opened." }).ToArray();
            foreach (var value in expired) _records[value.RequestId] = _records[value.RequestId] with { Event = value };
        }
        if (expired.Length > 0) Changed?.Invoke();
        return expired;
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var id in _records.Keys) _cleared.Add(id);
            while (_cleared.Count > 80) _cleared.Remove(_cleared.First());
            _records.Clear();
        }
        Changed?.Invoke();
    }
}
