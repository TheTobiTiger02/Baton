using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Baton.Protocol;

namespace Baton.Host;

public sealed class SessionStaleMonitor(
    WebSocketSessionHub sessions,
    DiagnosticsLog diagnostics,
    ILogger<SessionStaleMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var closed = await sessions.CloseStaleAsync(
                DateTimeOffset.UtcNow.Subtract(StaleAfter),
                stoppingToken);
            foreach (var deviceId in closed)
            {
                diagnostics.Record(DiagnosticsCategory.Transport, "session.stale",
                    $"no traffic for {StaleAfter.TotalSeconds:0} s", deviceId, DiagnosticsSeverity.Warning);
            }

            if (closed.Count > 0)
            {
                logger.LogInformation("Closed {Count} stale Baton session(s).", closed.Count);
            }
        }
    }
}
