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
    private const uint VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27;

    private readonly Application _app;
    private HotkeyManager? _hotkeys;
    private NotifyIcon? _tray;
    private MainWindow? _main;
    private FlyoutWindow? _flyout;
    private ToastWindow? _toast;
    private string? _lastTargetDeviceId;
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
        Host.WindowStreams.SessionChanged += (title, started) =>
            app.Dispatcher.BeginInvoke(() => Shell.Streaming = started ? title : null);
    }

    public BatonRuntime Runtime { get; }
    public BatonHost Host => Runtime.Host;
    public HandoffCoordinator Coordinator => Runtime.Coordinator;
    public ShellViewModel Shell { get; }
    public IReadOnlyList<string> HotkeyProblems { get; private set; } = [];

    public async Task StartAsync(bool showWindow)
    {
        await Runtime.StartAsync();
        Shell.StartTicking();
        Coordinator.HandoffUpdated += handoff => _app.Dispatcher.BeginInvoke(() => ShowToast(handoff));
        Coordinator.PhoneStreamOffered += (deviceId, activity, offer) => _app.Dispatcher.BeginInvoke(() => ShowPhone(deviceId, activity, offer));
        Runtime.WelcomeBack += (phone, activity) => _app.Dispatcher.BeginInvoke(() =>
        {
            if (!UserSettings.SuggestOnReturn)
            {
                return;
            }

            _toast ??= new ToastWindow();
            _toast.ShowSuggestion(activity, $"Continue from {phone.Name}?", "Continue here",
                () => _ = Coordinator.PullAsync(phone.DeviceId, activity.Id));
        });

        _hotkeys = new HotkeyManager();
        var problems = new List<string>();
        if (!_hotkeys.Register(HotkeyManager.ModControl | HotkeyManager.ModAlt, VkRight, SendToPhone))
        {
            problems.Add("Ctrl+Alt+Right");
        }

        if (!_hotkeys.Register(HotkeyManager.ModControl | HotkeyManager.ModAlt, VkLeft, PullToPc))
        {
            problems.Add("Ctrl+Alt+Left");
        }

        if (!_hotkeys.Register(HotkeyManager.ModControl | HotkeyManager.ModAlt, VkUp, () => ShowFlyout(pinned: true)))
        {
            problems.Add("Ctrl+Alt+Up");
        }

        HotkeyProblems = problems;
        CreateTray();
        SingleInstance.Listen(() => _app.Dispatcher.BeginInvoke(ShowMain));

        if (showWindow)
        {
            ShowMain();
        }
    }

    /// <summary>
    /// Continues an activity on a device with the app chosen for it before; the first time (or
    /// when <paramref name="ask"/>) asks which app, when there is more than one way.
    /// </summary>
    public async Task ContinueAsync(ActivityViewModel activity, string targetDeviceId, bool ask)
    {
        var source = activity.Owner.DeviceId;
        var item = activity.Activity;
        var choice = ask ? null : Coordinator.Remembered(item, source, targetDeviceId);
        if (choice is null)
        {
            var options = Coordinator.Options(item, source, targetDeviceId);
            if (options.Count > 1 || (ask && options.Count > 0))
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

        _lastTargetDeviceId = source == Coordinator.LocalDeviceId ? targetDeviceId : source;
        if (source == Coordinator.LocalDeviceId)
        {
            await Coordinator.SendAsync(targetDeviceId, item.Id, choice: choice);
        }
        else
        {
            await Coordinator.PullAsync(source, item.Id, targetDeviceId, choice: choice);
        }
    }

    /// <summary>Ctrl+Alt+Right: this PC's top activity goes to the phone used last (or the only one online).</summary>
    public void SendToPhone()
    {
        var online = Shell.Phones.Where(phone => phone.Online).ToArray();
        var target = online.FirstOrDefault(phone => phone.DeviceId == _lastTargetDeviceId) ?? online.FirstOrDefault();
        if (target is null)
        {
            ShowToast(new HandoffEvent("", Coordinator.LocalDeviceId, "", "No phone connected", HandoffStatus.Failed,
                "Open Baton on your phone, or pair one from the Baton window."));
            return;
        }

        _lastTargetDeviceId = target.DeviceId;
        if (Shell.Local.Top is { } top)
        {
            _ = ContinueAsync(top, target.DeviceId, ask: false);
        }
        else
        {
            _ = Coordinator.SendAsync(target.DeviceId);
        }
    }

    /// <summary>Ctrl+Alt+Left: continue here whatever a connected phone is doing.</summary>
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
        window.Closed += (_, _) =>
        {
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
            : Host.Devices.GetDevice(deviceId)?.DisplayName ?? "your phone";

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
        Runtime.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
    }

    private void ShowToast(HandoffEvent handoff)
    {
        _toast ??= new ToastWindow();
        _toast.Show(handoff, NameOf(handoff.SourceDeviceId), NameOf(handoff.TargetDeviceId));
    }

    private void CreateTray()
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Baton.ico"))!.Stream;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Baton", null, (_, _) => ShowMain());
        menu.Items.Add("Pair a phone…", null, (_, _) => { ShowMain(); ShowPairing(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Baton", null, (_, _) => Quit());
        _tray = new NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, 16, 16),
            Text = "Baton",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                ShowFlyout(pinned: false);
            }
        };
    }
}
