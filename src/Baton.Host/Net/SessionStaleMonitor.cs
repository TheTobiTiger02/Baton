using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Baton.Protocol;

namespace Baton.Host;

public sealed class SessionStaleMonitor(
    WebSocketSessionHub sessions,
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
            if (closed > 0)
            {
                logger.LogInformation("Closed {Count} stale Baton session(s).", closed);
            }
        }
    }
}
