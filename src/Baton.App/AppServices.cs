using System.Windows;
using System.Windows.Forms;
using Baton.Host;
using Baton.Host.Handoff;
using Baton.Protocol;
using Application = System.Windows.Application;

namespace Baton.App;

/// <summary>Owns the host, the handoff coordinator and every window; the app's composition root.</summary>
internal sealed class AppServices : IDisposable
{
    private readonly Application _app;
    private HotkeyManager? _hotkeys;
    private NotifyIcon? _tray;
    private MainWindow? _main;
    private FlyoutWindow? _flyout;
    private ToastWindow? _toast;
    private static readonly TimeSpan RetargetWindow = TimeSpan.FromSeconds(2);

    private HotkeySend? _lastHotkeySend;
    private DateTime _suggestionsPausedUntil;
    private readonly Dictionary<string, Mirror.PhoneWindow> _phoneWindows = [];

    public AppServices(Application app)
    {
        _app = app;
        // BATON_STORAGE lets a development build share identity and pairings with the dev host.
        Runtime = new BatonRuntime(Environment.GetEnvironmentVariable("BATON_STORAGE"));
        Shell = new ShellViewModel(Coordinator, app.Dispatcher)
        {
            StopStreaming = new RelayCommand(_ => Host.WindowStreams.StopAll())
        };
        Shell.Continue = ContinueAsync;
        Shell.DefaultPhoneId = UserSettings.LastTargetDeviceId;
        Clipboard = new Mirror.ClipboardBridge(Host, app.Dispatcher);
        Host.WindowStreams.Quality = UserSettings.StreamQuality;
        Host.WindowStreams.StreamAudio = UserSettings.StreamAudio;
        Host.WindowStreams.SessionChanged += (deviceId, title, started) =>
        {
            app.Dispatcher.BeginInvoke(() => Shell.Streaming = started ? title : null);
            if (started)
            {
                Clipboard.Start(deviceId);
            }
            else
            {
                Clipboard.Stop(deviceId);
            }
        };
    }

    public BatonRuntime Runtime { get; }
    public BatonHost Host => Runtime.Host;
    public HandoffCoordinator Coordinator => Runtime.Coordinator;
    public ShellViewModel Shell { get; }
    public Updates Updates { get; } = new();
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    /// <summary>Copies travel between this PC and a phone while one shows the other.</summary>
    private Mirror.ClipboardBridge Clipboard { get; }
    public IReadOnlyList<string> HotkeyProblems { get; private set; } = [];

    public async Task StartAsync(bool showWindow)
    {
        await Runtime.StartAsync();
        Shell.StartTicking();
        Coordinator.HandoffUpdated += handoff => _app.Dispatcher.BeginInvoke(() => ShowToast(handoff));
        Coordinator.PhoneStreamOffered += (deviceId, activity, offer) => _app.Dispatcher.BeginInvoke(() => ShowPhone(deviceId, activity, offer));
        Runtime.WelcomeBack += (phone, activity) => _app.Dispatcher.BeginInvoke(() =>
        {
            if (!UserSettings.SuggestOnReturn || _suggestionsPausedUntil > DateTime.UtcNow)
            {
                return;
            }

            _toast ??= new ToastWindow();
            _toast.ShowSuggestion(activity, $"Continue from {phone.Name}?", "Continue here",
                () => _ = Coordinator.PullAsync(phone.DeviceId, activity.Id));
        });

        // A browser extension without a token asks once; allowing it here gives it one.
        Runtime.Browser.ApprovalRequested += (connectionId, browser) => _app.Dispatcher.BeginInvoke(() =>
        {
            _toast ??= new ToastWindow();
            _toast.ShowAction($"Allow {browser} to connect?", $"The Baton extension in {browser} wants to show its tabs here.", "\uE774",
                "Allow", () => _ = Runtime.Browser.ApproveAsync(connectionId));
        });

        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        CreateTray();
        StartUpdateChecks();
        SingleInstance.Listen(() => _app.Dispatcher.BeginInvoke(ShowMain));

        if (showWindow)
        {
            ShowMain();
        }
    }

    /// <summary>Looks for a new release shortly after start and every few hours; offers a restart when one is ready.</summary>
    private void StartUpdateChecks()
    {
        if (!Updates.IsInstalled)
        {
            return;
        }

        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            await CheckForUpdatesAsync(quiet: true);
        };
        _updateTimer.Start();
    }

    /// <summary>Checks now. Returns what to tell the user; a found update also shows its restart toast.</summary>
    public async Task<string> CheckForUpdatesAsync(bool quiet = false)
    {
        if (!Updates.IsInstalled)
        {
            return "Updates come with the Setup.exe install. This copy was installed another way.";
        }

        try
        {
            if (await Updates.CheckAsync() is not { } version)
            {
                return "Baton is up to date.";
            }

            _toast ??= new ToastWindow();
            _toast.ShowAction($"Baton {version} is ready", "Restart Baton to finish updating.", "", "Restart to update", Updates.RestartToUpdate);
            return $"Baton {version} is ready. Restart to update.";
        }
        catch (Exception ex)
        {
            Host.Diagnostics.Record(DiagnosticsCategory.Handoff, "update.failed", ex.Message, severity: DiagnosticsSeverity.Warning);
            return quiet ? "" : "Couldn't check for updates. Check the internet connection and try again.";
        }
    }

    /// <summary>The phone sends go to first, from now on and after a restart: the one used last, or picked in Devices.</summary>
    public void MakeDefault(string deviceId)
    {
        UserSettings.LastTargetDeviceId = deviceId;
        Shell.DefaultPhoneId = deviceId;
    }

    public Hotkey HotkeyFor(HotkeyAction action) => UserSettings.GetHotkey(action);

    /// <summary>Changes a shortcut (null restores the default); false when another app owns the new one.</summary>
    public bool Rebind(HotkeyAction action, Hotkey? hotkey)
    {
        var previous = UserSettings.GetHotkey(action);
        UserSettings.SetHotkey(action, hotkey);
        RegisterHotkeys();
        if (!HotkeyProblems.Contains(HotkeyFor(action).ToString()))
        {
            return true;
        }

        UserSettings.SetHotkey(action, previous);
        RegisterHotkeys();
        return false;
    }

    private void RegisterHotkeys()
    {
        _hotkeys!.Clear();
        var problems = new List<string>();
        foreach (var (action, run) in new (HotkeyAction, Action)[]
                 {
                     (HotkeyAction.SendToPhone, SendToPhone),
                     (HotkeyAction.ContinueHere, PullToPc),
                     (HotkeyAction.Choose, () => ShowFlyout(pinned: true))
                 })
        {
            var hotkey = HotkeyFor(action);
            if (!_hotkeys.Register(hotkey.Modifiers, hotkey.VirtualKey, run))
            {
                problems.Add(hotkey.ToString());
            }
        }

        HotkeyProblems = problems;
        Shell.SendHotkey = HotkeyFor(HotkeyAction.SendToPhone).ToString();
        Shell.ContinueHotkey = HotkeyFor(HotkeyAction.ContinueHere).ToString();
        Shell.ChooseHotkey = HotkeyFor(HotkeyAction.Choose).ToString();
    }

    /// <summary>
    /// Continues an activity on a device with the app chosen for it before, else the best way.
    /// Only <paramref name="ask"/> ("Choose app…") shows the options.
    /// </summary>
    public async Task ContinueAsync(ActivityViewModel activity, string targetDeviceId, bool ask)
    {
        var source = activity.Owner.DeviceId;
        var item = activity.Activity;
        var choice = ask ? null : Coordinator.Remembered(item, source, targetDeviceId);
        if (choice is null)
        {
            var options = Coordinator.Options(item, source, targetDeviceId);
            if (ask && options.Count > 0)
            {
                choice = ContinueWithWindow.Ask(item.Title, Baton.Host.Apps.AppDirectory.SourceName(item), NameOf(targetDeviceId), options,
                    Coordinator.Remembered(item, source, targetDeviceId));
                if (choice is null)
                {
                    return;
                }
            }
            else
            {
                choice = options.FirstOrDefault();
            }
        }

        MakeDefault(source == Coordinator.LocalDeviceId ? targetDeviceId : source);
        if (source == Coordinator.LocalDeviceId)
        {
            await Coordinator.SendAsync(targetDeviceId, item.Id, choice: choice);
        }
        else
        {
            await Coordinator.PullAsync(source, item.Id, targetDeviceId, choice: choice);
        }
    }

    /// <summary>
    /// The send shortcut (Ctrl+Alt+Right): what the user is looking at on this PC goes to the phone used last (or the
    /// first online), without asking. Pressed again within <see cref="RetargetWindow"/>, it moves
    /// on to the next phone.
    /// </summary>
    public void SendToPhone()
    {
        var online = Shell.Phones.Where(phone => phone.Online).ToList();
        if (online.Count == 0)
        {
            ShowToast(new HandoffEvent("", Coordinator.LocalDeviceId, "", "No phone connected", HandoffStatus.Failed,
                "Open Baton on your phone, or pair one from the Baton window."));
            return;
        }

        var last = online.FindIndex(phone => phone.DeviceId == Shell.DefaultPhoneId);
        var previous = _lastHotkeySend is { } send && DateTime.UtcNow - send.At < RetargetWindow && online.Count > 1 ? send : null;
        var target = previous is not null ? online[(last + 1) % online.Count] : online[Math.Max(last, 0)];
        MakeDefault(target.DeviceId);
        _lastHotkeySend = new HotkeySend(DateTime.UtcNow, target.DeviceId);

        // The toast of each handoff names its phone ("Moving to S25…"). Pressed again, what went to
        // the previous phone moves on from there, so that phone stops playing it.
        if (previous is not null)
        {
            _ = Coordinator.PullAsync(previous.TargetDeviceId, targetDeviceId: target.DeviceId);
        }
        else
        {
            SendTo(target.DeviceId);
        }
    }

    /// <summary>
    /// The media or page in the window the user is in; a plain window stream only as the top item,
    /// so a video playing on another screen still beats the editor the hotkey was pressed in.
    /// </summary>
    private static ActivityViewModel? Focused(DeviceViewModel device) =>
        device.Activities.FirstOrDefault(activity => activity.Activity.Focused == true && activity.Activity.Kind != ActivityKind.WindowStream);

    /// <summary>The continue shortcut (Ctrl+Alt+Left): continue here whatever a connected phone is doing.</summary>
    public void PullToPc()
    {
        if (Shell.PhoneSuggestion is not { } suggestion)
        {
            ShowToast(new HandoffEvent("", "", Coordinator.LocalDeviceId, "Nothing to continue", HandoffStatus.Failed,
                "None of your connected phones is playing or showing anything Baton can continue."));
            return;
        }

        _ = ContinueAsync(suggestion, Coordinator.LocalDeviceId, ask: false);
    }

    public void ShowMain()
    {
        _main ??= new MainWindow(this);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
    }

    /// <summary>Opens (or reuses) the window a phone app continues in on this PC.</summary>
    public void ShowPhone(string deviceId, Activity activity, StreamOffer offer)
    {
        if (_phoneWindows.TryGetValue(deviceId, out var open))
        {
            if (open.SessionId == offer.SessionId)
            {
                open.Activate();
                return;
            }

            open.EndFromPhone();
        }

        var window = new Mirror.PhoneWindow(Host, deviceId, NameOf(deviceId), activity, offer);
        var clipboardStarted = false;
        window.PhoneReady += () =>
        {
            if (!clipboardStarted)
            {
                clipboardStarted = true;
                Clipboard.Start(deviceId);
            }
        };
        window.Closed += (_, _) =>
        {
            if (clipboardStarted)
            {
                Clipboard.Stop(deviceId);
            }

            if (_phoneWindows.TryGetValue(deviceId, out var current) && ReferenceEquals(current, window))
            {
                _phoneWindows.Remove(deviceId);
            }
        };
        _phoneWindows[deviceId] = window;
        if (Environment.GetEnvironmentVariable("BATON_TEST_OFFSCREEN") == "1")
        {
            // Automated tests run on a PC someone may be using: keep test windows out of sight.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -6000;
            window.Top = 0;
            window.ShowActivated = false;
            window.Show();
            return;
        }

        window.Show();
        window.Activate();
    }

    public void ShowPairing() => new PairingWindow(this) { Owner = _main?.IsVisible == true ? _main : null }.Show();

    public void ShowFlyout(bool pinned)
    {
        _flyout ??= new FlyoutWindow(this);
        _flyout.ShowNearTray(pinned);
    }

    public string NameOf(string deviceId) =>
        deviceId == Coordinator.LocalDeviceId
            ? "this PC"
            : Host.NameOf(deviceId) ?? "your phone";

    public void Quit()
    {
        _tray?.Dispose();
        _tray = null;
        _app.Shutdown();
    }

    public void Dispose()
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        Clipboard.Dispose();
        Runtime.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
    }

    private void ShowToast(HandoffEvent handoff)
    {
        _toast ??= new ToastWindow();
        _toast.Show(handoff, NameOf(handoff.SourceDeviceId), NameOf(handoff.TargetDeviceId));
    }

    private sealed record HotkeySend(DateTime At, string TargetDeviceId);

    private void CreateTray()
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Baton.ico"))!.Stream;
        _tray = new NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, 16, 16),
            Text = "Baton",
            Visible = true
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                ShowFlyout(pinned: false);
            }
        };
        _tray.MouseUp += (_, args) =>
        {
            if (args.Button == MouseButtons.Right)
            {
                ShowTrayMenu();
            }
        };
    }

    /// <summary>The notification-area menu, built fresh so it lists the phones online right now.</summary>
    private void ShowTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        void Add(string header, string glyph, Action onClick)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header, Icon = glyph };
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
        }

        var online = Shell.Phones.Where(phone => phone.Online).ToArray();
        foreach (var phone in online)
        {
            Add($"Send to {phone.Name}", "", () => SendTo(phone.DeviceId));
        }

        foreach (var phone in online.Where(phone => phone.Top is not null))
        {
            Add($"Continue from {phone.Name}", "", () => _ = ContinueAsync(phone.Top!, Coordinator.LocalDeviceId, ask: false));
        }

        if (online.Length > 0)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
        }

        if (_suggestionsPausedUntil > DateTime.UtcNow)
        {
            Add("Resume suggestions", "", () => _suggestionsPausedUntil = DateTime.MinValue);
        }
        else
        {
            Add("Pause suggestions for 1 hour", "", () => _suggestionsPausedUntil = DateTime.UtcNow.AddHours(1));
        }

        Add("Pair a phone…", "", () => { ShowMain(); ShowPairing(); });
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Open Baton", "", ShowMain);
        Add("Settings", "", () => { ShowMain(); _main!.ShowSettings(); });
        Add("Quit Baton", "", Quit);

        menu.IsOpen = true;
        // A menu of a background app only closes on an outside click once it owns the foreground.
        if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource source)
        {
            SetForegroundWindow(source.Handle);
        }
    }

    /// <summary>What this PC is showing, sent to one phone: the tray menu's "Send to".</summary>
    private void SendTo(string deviceId)
    {
        MakeDefault(deviceId);
        if ((Focused(Shell.Local) ?? Shell.Local.Top) is { } item)
        {
            _ = ContinueAsync(item, deviceId, ask: false);
        }
        else
        {
            _ = Coordinator.SendAsync(deviceId);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);
}
