using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Baton.Host;
using Baton.Host.Browser;
using Baton.Host.Handoff;
using Baton.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Baton.Tests;

public class BrowserHandoffBridgeTests
{
    [Fact]
    public async Task ApprovedConnectionsReceiveOnlyTheirOwnCorrelatedResultsAndQueries()
    {
        using var http = new HttpClient();
        var bridge = new BrowserBridge(new DiagnosticsLog(), http);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Run(bridge.HandleAsync);
        await app.StartAsync();
        using var first = new ClientWebSocket();
        using var second = new ClientWebSocket();
        try
        {
            var address = new Uri(app.Urls.Single().Replace("http:", "ws:") + "/browser");
            foreach (var socket in new[] { first, second })
            {
                socket.Options.SetRequestHeader("Origin", BrowserBridge.Origin);
                await socket.ConnectAsync(address, CancellationToken.None);
                Assert.Equal("devices", (await Receive(socket)).GetProperty("type").GetString());
                await Send(socket, new { type = "hello", browser = "Test", version = "0.2.0" });
                Assert.True((await Receive(socket)).GetProperty("handoffStatus").GetBoolean());
            }
            var requested = new TaskCompletionSource<BrowserSendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.CorrelatedSendRequested += request => requested.TrySetResult(request);
            var clientId = Guid.NewGuid().ToString();
            await Send(first, new { type = "send", requestId = clientId, tabId = 7, targetDeviceId = "phone" });
            var request = await requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotEqual(clientId, request.RequestId);
            var value = new HandoffEvent(request.RequestId, "pc", "phone", "Page", HandoffStatus.Failed, "Offline");
            bridge.FindHandoff = id => id == request.RequestId ? new HandoffRecord(value, DateTimeOffset.UtcNow, null) : null;
            bridge.PublishHandoff(value);
            var result = await Receive(first);
            Assert.Equal(clientId, result.GetProperty("requestId").GetString());
            Assert.Equal("failed", result.GetProperty("status").GetString());

            // A device update is a receive barrier: another browser must not have queued our result.
            bridge.PublishDevices([]);
            Assert.Equal("devices", (await Receive(second)).GetProperty("type").GetString());
            Assert.Equal("devices", (await Receive(first)).GetProperty("type").GetString());
            await Send(second, new { type = "handoffQuery", requestId = clientId });
            await Send(second, new { type = "hello", browser = "Test", version = "0.2.0" });
            Assert.Equal("capabilities", (await Receive(second)).GetProperty("type").GetString());
            await Send(first, new { type = "handoffQuery", requestId = clientId });
            Assert.Equal(clientId, (await Receive(first)).GetProperty("requestId").GetString());
            await Send(first, new { type = "tabs", tabs = new[] { new { tabId = 7, active = true,
                url = "https://example.com/", title = "Original", updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } } });
            await Send(first, new { type = "hello", browser = "Test", version = "0.2.0" });
            Assert.Equal("capabilities", (await Receive(first)).GetProperty("type").GetString());
            var snapshot = bridge.TakeAsync(request.ActivityId!, false, CancellationToken.None);
            Assert.Equal("take", (await Receive(first)).GetProperty("type").GetString());
            Assert.Null(await snapshot); // Silence cannot substitute a cached browser activity.
            await Send(second, new { type = "handoffRetry", requestId = Guid.NewGuid().ToString(), previousRequestId = clientId });
            Assert.Equal("failed", (await Receive(second)).GetProperty("status").GetString());
        }
        finally
        {
            first.Abort(); second.Abort();
            await app.StopAsync();
        }
    }

    private static async Task Send(ClientWebSocket socket, object value) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> Receive(ClientWebSocket socket)
    {
        var buffer = new byte[16_384];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.True(result.EndOfMessage);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }
}
