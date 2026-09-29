using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Host.Handoff;
using Baton.Host.Media;
using Baton.Protocol;
using Microsoft.AspNetCore.Http;

namespace Baton.Host.Browser;

/// <summary>
/// The Baton browser extension's end of the link. Browsers report their playing and active tabs
/// with exact URLs and positions, which beats the title-only view of the system media session, and
/// they pause, snapshot and seek tabs on request.
///
/// The extension connects over loopback WebSocket. Browsers send the extension's own origin with
/// the upgrade and web pages cannot forge it, so the origin check is what keeps sites out.
/// </summary>
public sealed class BrowserBridge(DiagnosticsLog diagnostics, HttpClient http) : IActivitySource, IActivityControl
{
    public const string ExtensionId = "bnmldmbdaaeaaojpcbjojfhknnfkdocn";
    public const string Origin = "chrome-extension://" + ExtensionId;
    private static readonly TimeSpan TakeTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TabState?>> _takes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _resumes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _artwork = new(StringComparer.Ordinal);
    private IReadOnlyList<BridgeDevice> _devices = [];

    /// <summary>
    /// The secret baked into Firefox builds of the extension. Firefox gives every extension a
    /// random origin, so the origin alone can't tell Baton's from any other; the token can.
    /// </summary>
    public string? TokenPath { get; set; }

    /// <summary>Browsers the user allowed; set by the runtime.</summary>
    public BrowserApprovals? Approvals { get; set; }

    /// <summary>An extension without a token asks to connect: (connection id, browser name). Any thread.</summary>
    public event Action<string, string>? ApprovalRequested;

    /// <summary>Browsers waiting for the user to allow them: (connection id, browser name).</summary>
    public IReadOnlyList<(string ConnectionId, string Browser)> PendingApprovals =>
        _connections.Values.Where(connection => connection.PendingHello is not null).Select(connection => (connection.Id, connection.Browser)).ToArray();

    /// <summary>The user allowed a waiting browser: it gets its token and continues as if it had presented one.</summary>
    public async Task ApproveAsync(string connectionId)
    {
        if (Approvals is null || !_connections.TryGetValue(connectionId, out var connection) || connection.PendingHello is not { } hello)
        {
            return;
        }

        var token = Approvals.Approve(connection.Browser);
        connection.PendingHello = null;
        connection.Proven = true;
        diagnostics.Record(DiagnosticsCategory.Media, "browser.approved", connection.Browser);
        await SendAsync(connection, new { type = "token", token });
        await SendAsync(connection, new { type = "devices", devices = _devices });
        OnMessage(connection, hello);
        Changed?.Invoke();
    }

    /// <summary>A token from an earlier approval, or the one baked into a development build.</summary>
    private bool IsKnownToken(string? token) =>
        !string.IsNullOrEmpty(token)
        && ((Approvals?.IsApproved(token) ?? false)
            || (Token() is { } legacy && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(legacy))));
    private int _nextConnection;

    public event Action? Changed;

    /// <summary>The popup or context menu asked to send a tab (by activity id) to a device.</summary>
    public event Action<string, string>? SendRequested;

    /// <summary>The context menu asked to send a bare link to a device.</summary>
    public event Action<Activity, string>? SendUrlRequested;

    public bool IsConnected => ProvenConnections.Any();

    public IReadOnlyList<string> ConnectedBrowsers => ProvenConnections.Select(connection => connection.Browser).Distinct().ToArray();

    /// <summary>Connections that may see devices and receive requests: allowed browsers only.</summary>
    private IEnumerable<Connection> ProvenConnections => _connections.Values.Where(connection => connection.Proven);

    public IReadOnlyList<Activity> Current =>
        _connections.Values.SelectMany(connection => connection.Tabs.Select(tab => ToActivity(connection, tab))).ToArray();

    /// <summary>
    /// True when a browser tab already reports the same media as a system media session, so the
    /// title-only session is hidden in favour of the tab with its exact URL.
    /// </summary>
    public bool Covers(Activity sessionActivity) =>
        sessionActivity.Content?.Provider is "web" or "unknown"
        && _connections.Values.Any(connection => connection.Tabs.Any(tab =>
            tab.Media is { } media && MediaOpener.TitlesMatch(media.Title ?? tab.Title ?? string.Empty, sessionActivity.Title)));

    public async Task HandleAsync(HttpContext context)
    {
        // Only extensions: web pages can't claim an extension origin. Baton's own development
        // build (fixed id) is trusted at once; any other extension (the store builds, Firefox's
        // random origins) proves itself with a token, or asks the user once for one.
        var origin = context.Request.Headers.Origin.ToString();
        var trusted = string.Equals(origin, Origin, StringComparison.OrdinalIgnoreCase);
        var extension = trusted
            || origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
        if (!context.WebSockets.IsWebSocketRequest || !extension)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new Connection($"b{Interlocked.Increment(ref _nextConnection)}", socket)
        {
            Proven = trusted,
            Process = LoopbackOwner.ProcessName(
                new System.Net.IPEndPoint(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback, context.Connection.RemotePort),
                context.Connection.LocalPort)
        };
        _connections[connection.Id] = connection;
        using var pinger = new PeriodicTimer(TimeSpan.FromSeconds(20));
        _ = PingAsync(connection, pinger, context.RequestAborted);
        if (connection.Proven)
        {
            await SendAsync(connection, new { type = "devices", devices = _devices });
        }

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var text = await ReceiveAsync(socket, context.RequestAborted);
                if (text is null)
                {
                    break;
                }

                try
                {
                    var message = JsonDocument.Parse(text).RootElement;
                    if (!connection.Proven)
                    {
                        // Until proven, only a hello counts: with a token Baton gave out, or it waits for the user.
                        if (message.GetProperty("type").GetString() != "hello")
                        {
                            continue;
                        }

                        var token = message.TryGetProperty("token", out var presented) ? presented.GetString() : null;
                        if (!IsKnownToken(token))
                        {
                            NameFromHello(connection, message);
                            var first = connection.PendingHello is null;
                            connection.PendingHello = message.Clone();
                            if (first)
                            {
                                diagnostics.Record(DiagnosticsCategory.Media, "browser.approval", $"{connection.Browser} asks to connect");
                                ApprovalRequested?.Invoke(connection.Id, connection.Browser);
                            }

                            await SendAsync(connection, new { type = "approval", state = "waiting" });
                            continue;
                        }

                        connection.Proven = true;
                        await SendAsync(connection, new { type = "devices", devices = _devices });
                    }

                    OnMessage(connection, message);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    diagnostics.Record(DiagnosticsCategory.Media, "browser.message.bad", ex.Message, severity: DiagnosticsSeverity.Warning);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            diagnostics.Record(DiagnosticsCategory.Media, "browser.disconnected", connection.Browser);
            Changed?.Invoke();
        }
    }

    /// <summary>Tells every browser which devices a tab can go to, for the popup.</summary>
    public void PublishDevices(IReadOnlyList<BridgeDevice> devices)
    {
        _devices = devices;
        foreach (var connection in ProvenConnections)
        {
            _ = SendAsync(connection, new { type = "devices", devices });
        }
    }

    /// <summary>
    /// Asks the browsers to seek the page at <paramref name="url"/> once its media loads. Used right
    /// after this PC opens a link handed over from a phone.
    /// </summary>
    public void ExpectSeek(string url, long positionMs)
    {
        foreach (var connection in ProvenConnections)
        {
            _ = SendAsync(connection, new { type = "expect", url, positionMs });
        }
    }

    /// <summary>
    /// Continues <paramref name="activity"/> in a tab that already shows it (the one it was sent
    /// from, usually) instead of opening another: same video, else same page, else same title.
    /// </summary>
    public async Task<bool> TryResumeAsync(Activity activity, long positionMs)
    {
        var videoId = ContentLinks.YouTubeVideoId(activity.Url) ?? (activity.Content?.Provider is "youtube" or "youtubemusic" ? activity.Content.Id : null);
        var best = _connections.Values
            .SelectMany(connection => connection.Tabs.Select(tab => (Connection: connection, Tab: tab, Score: Score(tab))))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Tab.UpdatedAt)
            .FirstOrDefault();
        if (best.Connection is null)
        {
            return false;
        }

        var url = videoId is not null ? ContentLinks.WithYouTubeTime(best.Tab.Url, positionMs) : best.Tab.Url;
        var requestId = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _resumes[requestId] = reply;
        bool applied;
        try
        {
            await SendAsync(best.Connection, new { type = "resume", requestId, tabId = best.Tab.TabId, positionMs, url });
            // Only a tab that confirms the seek (or reloads at the link's time) counts; otherwise the
            // caller opens the page afresh at the right second. An older extension can't confirm.
            applied = !best.Connection.ConfirmsResume
                || await reply.Task.WaitAsync(TakeTimeout).ContinueWith(task => task.IsCompletedSuccessfully && task.Result);
        }
        finally
        {
            _resumes.TryRemove(requestId, out _);
        }

        if (applied && Baton.Media.DesktopWindows.MainWindowOf(ProcessOf(best.Connection)) is { } window)
        {
            Baton.Media.DesktopWindows.BringToFront(window.Handle);
        }

        diagnostics.Record(DiagnosticsCategory.Handoff, applied ? "resume.tab" : "resume.tab.failed",
            $"{best.Connection.Browser} tab {best.Tab.TabId}: {best.Tab.Title}");
        return applied;

        int Score(TabState tab)
        {
            if (videoId is not null && ContentLinks.YouTubeVideoId(tab.Url) == videoId)
            {
                return 3;
            }

            if (activity.Url is not null && SamePage(activity.Url, tab.Url))
            {
                return 2;
            }

            return tab.Media is { } media && MediaOpener.TitlesMatch(media.Title ?? tab.Title ?? "", activity.Title) ? 1 : 0;
        }
    }

    /// <summary>
    /// The same page: host, path and query, ignoring only where in it one is (t=, start=...) and
    /// tracking tags. The query counts: /watch?v=a and /watch?v=b are different videos.
    /// </summary>
    public static bool SamePage(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var left) && Uri.TryCreate(b, UriKind.Absolute, out var right)
        && string.Equals(left.Host.Replace("www.", ""), right.Host.Replace("www.", ""), StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.AbsolutePath.TrimEnd('/'), right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
        && Query(left) == Query(right);

    private static string Query(Uri uri) => string.Join('&', uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Where(pair => pair.Split('=')[0] is not ("t" or "start" or "time_continue" or "si" or "feature" or "pp") && !pair.StartsWith("utm_", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal));

    /// <summary>Every Firefox-based browser says "Firefox"; its process says which one it is (Zen...).</summary>
    private static void NameFromHello(Connection connection, JsonElement hello) =>
        connection.Browser = hello.TryGetProperty("browser", out var browser) && browser.GetString() is { } named && named != "Firefox"
            ? named
            : connection.Process is { } process ? KnownApps.DisplayName(process) : "Firefox";

    private static string ProcessOf(Connection connection) => connection.Process ?? connection.Browser.ToLowerInvariant() switch
    {
        "edge" => "msedge",
        var other => other
    };

    private string? Token()
    {
        try
        {
            return TokenPath is not null && File.Exists(TokenPath) ? File.ReadAllText(TokenPath).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<Activity?> TakeAsync(string activityId, bool pause, CancellationToken cancellationToken)
    {
        var parts = activityId.Split(':');
        if (parts.Length != 3 || !_connections.TryGetValue(parts[1], out var connection) || !int.TryParse(parts[2], out var tabId))
        {
            return null;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<TabState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _takes[requestId] = reply;
        try
        {
            await SendAsync(connection, new { type = "take", requestId, tabId, pause });
            var tab = await reply.Task.WaitAsync(TakeTimeout, cancellationToken);
            if (tab is null)
            {
                return null;
            }

            tab = tab with { TabId = tabId, UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            // Known paused here right away, not only when the extension's next report arrives.
            connection.Tabs = connection.Tabs.Select(known => known.TabId == tabId ? tab with { Active = known.Active, Audible = known.Audible } : known).ToArray();
            Changed?.Invoke();
            return ToActivity(connection, tab);
        }
        catch (TimeoutException)
        {
            return connection.Tabs.FirstOrDefault(tab => tab.TabId == tabId) is { } known ? ToActivity(connection, known) : null;
        }
        finally
        {
            _takes.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Pauses every tab playing media titled <paramref name="title"/>: the fallback when pausing a
    /// system media session did not take. True when a tab was asked to.
    /// </summary>
    public async Task<bool> PauseMatchingAsync(string title)
    {
        var asked = false;
        foreach (var connection in _connections.Values)
        {
            foreach (var tab in connection.Tabs.Where(tab => tab.Media is { Playing: true } media
                         && MediaOpener.TitlesMatch(media.Title ?? tab.Title ?? string.Empty, title)))
            {
                await SendAsync(connection, new { type = "command", tabId = tab.TabId, action = MediaActions.Pause });
                asked = true;
            }
        }

        return asked;
    }

    /// <summary>Plays, pauses, seeks or sets the volume of a tab's media, through the extension.</summary>
    public async Task<bool> CommandAsync(Activity activity, MediaCommandPayload command, CancellationToken cancellationToken)
    {
        var parts = activity.Id.Split(':');
        if (parts.Length != 3 || !_connections.TryGetValue(parts[1], out var connection) || !int.TryParse(parts[2], out var tabId))
        {
            return false;
        }

        await SendAsync(connection, new { type = "command", tabId, action = command.Action, positionMs = command.PositionMs, volume = command.Volume });
        diagnostics.Record(DiagnosticsCategory.Media, "remote.command", $"{command.Action} on tab '{activity.Title}'");
        return true;
    }

    private void OnMessage(Connection connection, JsonElement message)
    {
        switch (message.GetProperty("type").GetString())
        {
            case "hello":
                NameFromHello(connection, message);
                connection.ConfirmsResume = message.TryGetProperty("version", out var version)
                    && Version.TryParse(version.GetString(), out var parsed) && parsed >= new Version(0, 1, 2);
                diagnostics.Record(DiagnosticsCategory.Media, "browser.connected", connection.Browser);
                break;
            case "tabs":
                connection.Tabs = message.GetProperty("tabs").Deserialize<TabState[]>(JsonOptions) ?? [];
                foreach (var tab in connection.Tabs)
                {
                    FetchArtwork(tab.Media?.Artwork);
                }

                Changed?.Invoke();
                break;
            case "resumed":
            {
                var requestId = message.GetProperty("requestId").GetString() ?? string.Empty;
                if (_resumes.TryGetValue(requestId, out var resumed))
                {
                    resumed.TrySetResult(message.TryGetProperty("applied", out var applied) && applied.ValueKind == JsonValueKind.True);
                }

                break;
            }
            case "taken":
            {
                var requestId = message.GetProperty("requestId").GetString() ?? string.Empty;
                var tab = message.TryGetProperty("tab", out var value) && value.ValueKind == JsonValueKind.Object
                    ? value.Deserialize<TabState>(JsonOptions)
                    : null;
                if (_takes.TryGetValue(requestId, out var reply))
                {
                    reply.TrySetResult(tab);
                }

                break;
            }
            case "send":
                SendRequested?.Invoke($"tab:{connection.Id}:{message.GetProperty("tabId").GetInt32()}", message.GetProperty("targetDeviceId").GetString()!);
                break;
            case "log":
                diagnostics.Record(DiagnosticsCategory.Media, "browser.log", message.GetProperty("text").GetString(), connection.Browser);
                break;
            case "sendUrl":
            {
                var url = message.GetProperty("url").GetString()!;
                var activity = new Activity($"link:{Guid.NewGuid():N}", string.Empty, ActivityKind.WebPage, url,
                    new ActivityApp(connection.Browser, connection.Browser.ToLowerInvariant()), DateTimeOffset.UtcNow, Url: url);
                SendUrlRequested?.Invoke(activity, message.GetProperty("targetDeviceId").GetString()!);
                break;
            }
        }
    }

    private Activity ToActivity(Connection connection, TabState tab)
    {
        var media = tab.Media;
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(tab.UpdatedAt);
        var (provider, contentId) = ClassifyUrl(tab.Url);
        string? host = Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", string.Empty) : null;
        return new Activity(
            $"tab:{connection.Id}:{tab.TabId}",
            string.Empty,
            media is null ? ActivityKind.WebPage : ActivityKind.WebMedia,
            (media?.Title ?? tab.Title ?? tab.Url).Trim(),
            new ActivityApp(connection.Browser, connection.Browser.ToLowerInvariant()),
            // Since it started playing (older extensions don't say: then now).
            media?.Playing == true
                ? tab.PlayingSince is { } since ? DateTimeOffset.FromUnixTimeMilliseconds(since) : DateTimeOffset.UtcNow
                : updatedAt,
            Subtitle: media?.Artist ?? host,
            ArtworkJpegBase64: media?.Artwork is { } art && _artwork.TryGetValue(art, out var jpeg) ? jpeg : null,
            Url: tab.Url,
            Content: new ActivityContent(provider, contentId, media?.Title ?? tab.Title),
            Playback: media is null ? null : new Playback(media.PositionMs, media.DurationMs, media.Playing, media.Rate <= 0 ? 1 : media.Rate, updatedAt),
            Volume: media?.Volume,
            // A player muted in the page is silent even when the tab isn't.
            Audible: media is null ? null : media.Volume == 0 ? false : tab.Audible,
            // The active tab of the browser window the user was last in.
            Focused: tab.Active);
    }

    /// <summary>Which service a URL belongs to, and the item's id there when it is in the URL.</summary>
    public static (string Provider, string? Id) ClassifyUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return ("web", null);
        }

        var host = uri.Host.ToLowerInvariant();
        return host switch
        {
            "music.youtube.com" => ("youtubemusic", ContentLinks.YouTubeVideoId(url)),
            _ when host.EndsWith("youtube.com") || host == "youtu.be" => ("youtube", ContentLinks.YouTubeVideoId(url)),
            _ when host.EndsWith("netflix.com") => ("netflix", uri.AbsolutePath.StartsWith("/watch/") ? uri.Segments.LastOrDefault() : null),
            _ when host.EndsWith("twitch.tv") => ("twitch", null),
            _ when host.EndsWith("disneyplus.com") => ("disney", null),
            _ when host.Contains("primevideo") || host.EndsWith("amazon.com") || host.EndsWith("amazon.de") => ("prime", null),
            _ when host == "open.spotify.com" => ("spotify", null),
            _ => ("web", null)
        };
    }

    private void FetchArtwork(string? url)
    {
        if (url is null || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !_artwork.TryAdd(url, null))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url);
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
                stream.Seek(0);
                _artwork[url] = await Artwork.ToJpegBase64Async(stream);
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // No artwork is fine; the card shows the site's glyph instead.
            }
        });
    }

    private static async Task PingAsync(Connection connection, PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SendAsync(connection, new { type = "ping" });
            }
        }
        catch (Exception)
        {
            // The socket closed; the receive loop cleans up.
        }
    }

    private static async Task SendAsync(Connection connection, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await connection.Writer.WaitAsync();
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            connection.Writer.Release();
        }
    }

    private static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 1024 * 1024)
            {
                return null;
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            }
        }
    }

    private sealed class Connection(string id, WebSocket socket)
    {
        public string Id { get; } = id;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim Writer { get; } = new(1, 1);
        public string Browser { get; set; } = "Browser";
        public IReadOnlyList<TabState> Tabs { get; set; } = [];

        /// <summary>The browser's process name (zen, msedge...), when it could be found.</summary>
        public string? Process { get; init; }

        /// <summary>False until the hello carried a token Baton gave out (or the development id connected).</summary>
        public bool Proven { get; set; } = true;

        /// <summary>The hello of an extension waiting for the user's approval; handled once allowed.</summary>
        public JsonElement? PendingHello { get; set; }

        /// <summary>The extension answers a resume with whether the seek applied (0.1.2 and later).</summary>
        public bool ConfirmsResume { get; set; }
    }

    public sealed record TabState(int TabId, bool Active, string Url, string? Title, MediaState? Media, long UpdatedAt, bool? Audible = null,
        long? PlayingSince = null);

    public sealed record MediaState(string? Title, string? Artist, string? Artwork, long PositionMs, long DurationMs, bool Playing, double Rate, bool Live, double? Volume = null);
}

public sealed record BridgeDevice(string DeviceId, string Name, bool Online);
