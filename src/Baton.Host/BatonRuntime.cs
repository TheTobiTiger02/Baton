using Baton.Host.Browser;
using Baton.Host.Handoff;
using Baton.Host.Media;
using Baton.Media;
using Baton.Protocol;

namespace Baton.Host;

/// <summary>
/// Everything that makes this PC a Baton device, wired together: the host, every activity source
/// (browser tabs, media sessions, windows), the openers, and the coordinator. The desktop app and
/// the headless dev host both run exactly this.
/// </summary>
public sealed class BatonRuntime : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public BatonRuntime(string? storageDirectory = null)
    {
        // Physical pixels everywhere: window bounds, capture and input must agree on every monitor.
        SetProcessDpiAwarenessContext(new IntPtr(-4) /* PER_MONITOR_AWARE_V2 */);
        Host = new BatonHost(storageDirectory);
        var youtube = new YouTubeResolver(_http);
        Media = new MediaSessionSource(Host.Diagnostics, Host.Files);
        Browser = new BrowserBridge(Host.Diagnostics, _http);
        Windows = new WindowSource(Host.Files);
        Host.BrowserEndpoint = Browser.HandleAsync;
        Media.IsCoveredElsewhere = Browser.Covers;
        Browser.TokenPath = BrowserToken.EnsureFile(Host.StorageDirectory);
        Browser.Approvals = new BrowserApprovals(Path.Combine(Host.StorageDirectory, "browser-approvals.json"));
        Apps = new Baton.Host.Apps.AppDirectory(Host.StorageDirectory, Host.Diagnostics) { ResumeExisting = ResumeExistingAsync };
        var mediaOpener = new MediaOpener(Media, youtube, Host.Diagnostics, Browser.ExpectSeek, ResumeExistingAsync);
        Coordinator = new HandoffCoordinator(
            Host,
            [Browser, Media, Windows],
            [
                new LocalMediaOpener(Host.Files, Media, Host.Diagnostics),
                mediaOpener
            ],
            new ActivityEnricher(youtube, Host.Diagnostics, new StremioResolver(_http)),
            Apps)
        {
            OfferStream = (activity, target, sessionId) => activity.Window is { } window
                ? Host.WindowStreams.Start(target, DesktopWindows.FromToken(window.WindowToken), activity.Title, sessionId)
                : null,
            // Both ways, whichever the activity came from: pausing an already paused player is harmless.
            PauseElsewhere = async activity => await Browser.PauseMatchingAsync(activity.Title) | await Media.PauseMatchingAsync(activity.Title),
            OpenInBrowser = (activity, browser) => mediaOpener.TryOpenAsync(activity, browser, CancellationToken.None)
        };
        Presence.Changed += Coordinator.SetLocalPresence;
        Presence.Returned += away =>
        {
            if (Coordinator.SuggestionForReturn() is { } suggestion)
            {
                WelcomeBack?.Invoke(suggestion.Device, suggestion.Activity);
            }
        };
        Browser.SendRequested += (activityId, target) => _ = Coordinator.SendAsync(target, activityId);
        Browser.SendUrlRequested += (activity, target) => _ = Coordinator.SendActivityAsync(target, activity);
        Coordinator.DevicesChanged += PublishDevicesToBrowsers;
    }

    /// <summary>The phone sends go to first (the app's default), for the browsers' menus.</summary>
    public Func<string?>? DefaultPhone { get; set; }

    /// <summary>Tells the browser extensions which phones there are, the default one marked.</summary>
    public void PublishDevicesToBrowsers()
    {
        var preferred = DefaultPhone?.Invoke();
        Browser.PublishDevices(Coordinator.GetDevices()
            .Where(device => device.Kind == DeviceKinds.Phone)
            .Select(device => new BridgeDevice(device.DeviceId, device.Name, device.Online, device.DeviceId == preferred))
            .ToArray());
    }

    /// <summary>Something coming back continues in the tab or app that still has it, when one does.</summary>
    private async Task<bool> ResumeExistingAsync(Activity activity, long positionMs) =>
        await Browser.TryResumeAsync(activity, positionMs) || await Media.TryResumeAsync(activity, positionMs);

    public BatonHost Host { get; }
    public MediaSessionSource Media { get; }
    public BrowserBridge Browser { get; }
    public WindowSource Windows { get; }
    public HandoffCoordinator Coordinator { get; }
    public Baton.Host.Apps.AppDirectory Apps { get; }
    public PresenceMonitor Presence { get; } = new();

    /// <summary>
    /// The user came back to this PC while a phone is playing something: offer to continue it
    /// here. Any thread.
    /// </summary>
    public event Action<DeviceView, Activity>? WelcomeBack;

    public async Task StartAsync()
    {
        await Host.StartAsync();
        await Media.StartAsync();
        _ = Apps.Pc.RefreshAsync();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public async ValueTask DisposeAsync()
    {
        Presence.Dispose();
        Windows.Dispose();
        Media.Dispose();
        await Host.DisposeAsync();
        _http.Dispose();
    }
}
