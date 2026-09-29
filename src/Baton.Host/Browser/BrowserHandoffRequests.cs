namespace Baton.Host.Browser;

/// <summary>Correlations are scoped to one approved connection, never broadcast to other browsers.</summary>
public sealed class BrowserHandoffRequests
{
    private readonly object _gate = new();
    private readonly List<(string HostId, string ConnectionId, string ClientId)> _requests = [];

    public string Add(string connectionId, string clientId)
    {
        if (!Guid.TryParse(clientId, out _)) throw new ArgumentException("Invalid handoff request ID.");
        lock (_gate)
        {
            if (_requests.Any(r => r.ConnectionId == connectionId && r.ClientId == clientId))
                throw new ArgumentException("Handoff request ID already used.");
            var id = Guid.NewGuid().ToString("N");
            _requests.Add((id, connectionId, clientId));
            while (_requests.Count(r => r.ConnectionId == connectionId) > 40)
                _requests.RemoveAt(_requests.FindIndex(r => r.ConnectionId == connectionId));
            return id;
        }
    }

    public (string ConnectionId, string ClientId)? Find(string hostId)
    {
        lock (_gate)
        {
            var row = _requests.FirstOrDefault(r => r.HostId == hostId);
            return row.HostId is null ? null : (row.ConnectionId, row.ClientId);
        }
    }

    public string? HostId(string connectionId, string clientId)
    {
        lock (_gate) return _requests.FirstOrDefault(r => r.ConnectionId == connectionId && r.ClientId == clientId).HostId;
    }

    public void Remove(string connectionId)
    {
        lock (_gate) _requests.RemoveAll(r => r.ConnectionId == connectionId);
    }
}

public sealed record BrowserSendRequest(string RequestId, string TargetDeviceId, string? ActivityId, Baton.Protocol.Activity? Activity);
