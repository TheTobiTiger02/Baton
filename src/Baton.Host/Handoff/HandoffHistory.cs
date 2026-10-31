using Baton.Protocol;
using Baton.Host.Browser;

namespace Baton.Host.Handoff;

public sealed record HandoffIntent(string SourceDeviceId, string TargetDeviceId, string? ActivityId,
    string Mode, HandoffChoice? Choice, Activity? Activity = null, bool Standalone = false, BrowserReturnTarget? ReturnTab = null)
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

/// <summary>
/// Bounded handoff state: the last <see cref="MaxRecords"/> handoffs of the last
/// <see cref="Retention"/>, kept on disk encrypted for this Windows user when given a path, so
/// the timeline every device shares survives restarts. A timeout is uncertainty, not a failed open.
/// </summary>
public sealed class HandoffHistory
{
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public const int MaxRecords = 40;
    private readonly object _gate = new();
    private readonly HashSet<string> _cleared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandoffRecord> _records = new(StringComparer.Ordinal);
    private readonly string? _path;
    public event Action? Changed;

    public HandoffHistory(string? path = null)
    {
        _path = path;
        foreach (var record in Load(path))
        {
            // Whatever was still in flight when Baton stopped never got its answer.
            _records[record.Event.RequestId] = record.Event.Status is null && !record.Event.Unconfirmed
                ? record with { Event = record.Event with { Unconfirmed = true, Detail = "No confirmation received. The activity may already have opened." } }
                : record;
        }
    }

    /// <summary>The timeline as every device gets it, newest first.</summary>
    public IReadOnlyList<TimelineEntry> Timeline() => Recent.Select(record => new TimelineEntry(
        record.Event.RequestId, record.Event.Title, record.StartedAt, record.Event.SourceDeviceId, record.Event.TargetDeviceId,
        record.Event.Status, record.Event.Detail, record.Event.Unconfirmed, record.Intent?.Activity is { } activity ? Bare(activity) : null)).ToArray();

    public IReadOnlyList<HandoffRecord> Recent
    {
        get { lock (_gate) return _records.Values.OrderByDescending(item => item.StartedAt).ToArray(); }
    }

    public HandoffRecord? Find(string id)
    {
        lock (_gate) return _records.GetValueOrDefault(id);
    }

    public HandoffRecord? Continuation(string? id, string phoneId)
    {
        if (id is null) return null;
        lock (_gate)
        {
            var record = _records.GetValueOrDefault(id);
            return record is { Intent.ReturnTab: not null, Event.Status: HandoffStatus.Opened or HandoffStatus.Fallback }
                && record.Intent.TargetDeviceId == phoneId && !record.Intent.Streaming ? record : null;
        }
    }

    public void UpdateReturnTab(string id, int tabId, string url)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(id, out var record) && record.Intent?.ReturnTab is { } target)
                _records[id] = record with { Intent = record.Intent with { ReturnTab = target with { TabId = tabId, Url = url } } };
        }
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
            var cutoff = (now ?? DateTimeOffset.UtcNow) - Retention;
            foreach (var oldest in _records.Values.OrderByDescending(item => item.StartedAt)
                .Where((item, index) => index >= MaxRecords || item.StartedAt < cutoff).ToArray())
            {
                _records.Remove(oldest.Event.RequestId);
                _cleared.Add(oldest.Event.RequestId);
            }
            while (_cleared.Count > 80) _cleared.Remove(_cleared.First());
            SaveLocked();
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
            if (expired.Length > 0) SaveLocked();
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
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>The activity without its picture: the timeline travels and is stored, the artwork needn't be.</summary>
    private static Activity Bare(Activity activity) => activity with { ArtworkJpegBase64 = null };

    private void SaveLocked()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var stored = _records.Values.Select(record => record with { Intent = record.Intent is { Activity: { } activity } intent ? intent with { Activity = Bare(activity) } : record.Intent });
            var json = System.Text.Json.JsonSerializer.Serialize(stored.ToArray(), Json.Options);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, SecretProtector.Protect(json));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Kept in memory; the next change tries again.
        }
    }

    private static IEnumerable<HandoffRecord> Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = SecretProtector.Unprotect(File.ReadAllText(path));
            var cutoff = DateTimeOffset.UtcNow - Retention;
            return (System.Text.Json.JsonSerializer.Deserialize<HandoffRecord[]>(json, Json.Options) ?? [])
                .Where(record => record.Event?.RequestId is { Length: > 0 } && record.StartedAt >= cutoff)
                .OrderByDescending(record => record.StartedAt).Take(MaxRecords).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or System.Security.Cryptography.CryptographicException or FormatException)
        {
            // A damaged or foreign file: start an empty timeline.
            return [];
        }
    }
}
