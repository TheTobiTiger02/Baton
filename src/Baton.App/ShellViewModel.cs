using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Baton.Host.Handoff;
using Baton.Protocol;

namespace Baton.App;

/// <summary>Where a card shows an action: its one accent button, a plain button, an icon, or the "⋯" menu.</summary>
public enum ActionPlacement { Primary, Secondary, Control, Menu }

public sealed record ActivityAction(string Label, string Glyph, ICommand Command, ActionPlacement Placement)
{
    public bool IsPrimary => Placement == ActionPlacement.Primary;
    public bool IsControl => Placement == ActionPlacement.Control;
}

/// <summary>Everything the windows show, rebuilt from <see cref="HandoffCoordinator.GetDevices"/>.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly HandoffCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ticker;
    private readonly Dictionary<string, IReadOnlyList<ActivityAction>> _actionCache = [];

    public ShellViewModel(HandoffCoordinator coordinator, Dispatcher dispatcher)
    {
        _coordinator = coordinator;
        _dispatcher = dispatcher;
        Local = new DeviceViewModel(coordinator.LocalDeviceId, DeviceKinds.Pc);
        coordinator.DevicesChanged += () => _dispatcher.BeginInvoke(Refresh);
        if (coordinator.Apps is { } apps)
        {
            // Labels name the app a phone opens, which depends on the choices and the phone's apps.
            void Reset() => _dispatcher.BeginInvoke(() =>
            {
                _actionCache.Clear();
                Refresh();
            });
            apps.PreferencesChanged += Reset;
            apps.CatalogChanged += Reset;
        }
        _ticker = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        Refresh();
    }

    private string? _streaming;
    private string? _defaultPhoneId;

    /// <summary>The phone sends go to first. Set by the app.</summary>
    public string? DefaultPhoneId
    {
        get => _defaultPhoneId;
        set
        {
            if (Set(ref _defaultPhoneId, value))
            {
                _actionCache.Clear();
                Refresh();
            }
        }
    }
    private string _sendHotkey = "", _continueHotkey = "", _chooseHotkey = "";

    /// <summary>The shortcuts as the user set them, for the hints on screen.</summary>
    public string SendHotkey { get => _sendHotkey; set => Set(ref _sendHotkey, value); }
    public string ContinueHotkey { get => _continueHotkey; set => Set(ref _continueHotkey, value); }
    public string ChooseHotkey { get => _chooseHotkey; set => Set(ref _chooseHotkey, value); }

    /// <summary>The window being streamed to a phone right now, if any.</summary>
    public string? Streaming
    {
        get => _streaming;
        set
        {
            if (Set(ref _streaming, value))
            {
                Raise(nameof(IsStreaming));
            }
        }
    }

    public bool IsStreaming => _streaming is not null;

    public System.Windows.Input.ICommand? StopStreaming { get; init; }

    /// <summary>
    /// Continues an activity on a device (activity, target, ask): with the app chosen for it before,
    /// else after asking which (always when <c>ask</c>). Set by the app.
    /// </summary>
    public Func<ActivityViewModel, string, bool, Task>? Continue { get; set; }

    public DeviceViewModel Local { get; }
    public ObservableCollection<DeviceViewModel> Phones { get; } = [];
    public bool HasPhones => Phones.Count > 0;
    public bool HasOnlinePhone => Phones.Any(phone => phone.Online);

    /// <summary>The most useful single suggestion: the newest activity on a connected phone.</summary>
    public ActivityViewModel? PhoneSuggestion =>
        Phones.Where(phone => phone.Online).Select(phone => phone.Top).OfType<ActivityViewModel>()
            .OrderByDescending(activity => activity.IsPlaying)
            .ThenByDescending(activity => activity.Activity.UpdatedAt)
            .FirstOrDefault();

    public void StartTicking() => _ticker.Start();

    public void StopTicking() => _ticker.Stop();

    private IReadOnlyList<ActivityAction> ActionsFor(ActivityViewModel activity)
    {
        var key = $"{activity.Owner.DeviceId}|{activity.Id}|{activity.IsPlaying}|{string.Join(',', Phones.Where(p => p.Online).Select(p => p.DeviceId + p.Name))}";
        if (_actionCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var actions = new List<ActivityAction>();
        var item = activity.Activity;
        if (activity.Owner.IsPc)
        {
            var phones = Phones.Where(phone => phone.Online).OrderByDescending(phone => phone.IsDefault).ToArray();
            foreach (var phone in phones)
            {
                var chosen = _coordinator.Remembered(item, activity.Owner.DeviceId, phone.DeviceId);
                var streams = chosen is null ? HandoffCoordinator.ContinuesAsStream(item) && _coordinator.Options(item, activity.Owner.DeviceId, phone.DeviceId).FirstOrDefault()?.Kind is null or ChoiceKinds.Stream
                    : chosen.Kind == ChoiceKinds.Stream;
                var label = chosen is { Kind: not ChoiceKinds.Stream and not ChoiceKinds.Default } ? $"{phone.Name} · {chosen.Label}"
                    : streams ? $"Stream to {phone.Name}" : $"Send to {phone.Name}";
                actions.Add(new ActivityAction(label, streams ? "" : "",
                    new RelayCommand(_ => _ = Continue?.Invoke(activity, phone.DeviceId, false)),
                    actions.Count == 0 ? ActionPlacement.Primary : ActionPlacement.Secondary));
            }

            if (phones.Length > 0 && _coordinator.Options(item, activity.Owner.DeviceId, phones[0].DeviceId).Count > 1)
            {
                actions.Add(new ActivityAction("Choose app…", "",
                    new RelayCommand(_ => _ = Continue?.Invoke(activity, phones[0].DeviceId, true)), ActionPlacement.Menu));
            }

            // Native apps come first; the window itself can always be streamed instead.
            if (phones.Length > 0 && HandoffCoordinator.CanStream(item) && !HandoffCoordinator.ContinuesAsStream(item))
            {
                actions.Add(new ActivityAction("Stream instead", "",
                    new RelayCommand(_ => _ = _coordinator.SendAsync(phones[0].DeviceId, activity.Id, HandoffModes.Stream)), ActionPlacement.Menu));
            }
        }
        else
        {
            var chosen = _coordinator.Remembered(item, activity.Owner.DeviceId, _coordinator.LocalDeviceId);
            var mirrors = chosen?.Kind == ChoiceKinds.Stream || (chosen is null && item.Kind == ActivityKind.WindowStream
                && _coordinator.Options(item, activity.Owner.DeviceId, _coordinator.LocalDeviceId).FirstOrDefault()?.Kind is null or ChoiceKinds.Stream);
            var label = chosen is { Kind: ChoiceKinds.App or ChoiceKinds.Web } ? $"Continue in {chosen.Label}" : mirrors ? "Show here" : "Continue here";
            actions.Add(new ActivityAction(label, mirrors ? "" : "",
                new RelayCommand(_ => _ = Continue?.Invoke(activity, _coordinator.LocalDeviceId, false)), ActionPlacement.Primary));
            if (_coordinator.Options(item, activity.Owner.DeviceId, _coordinator.LocalDeviceId).Count > 1)
            {
                actions.Add(new ActivityAction("Choose app…", "",
                    new RelayCommand(_ => _ = Continue?.Invoke(activity, _coordinator.LocalDeviceId, true)), ActionPlacement.Menu));
            }
            if (!mirrors)
            {
                actions.Add(new ActivityAction("Mirror", "",
                    new RelayCommand(_ => _ = _coordinator.PullAsync(activity.Owner.DeviceId, activity.Id, mode: HandoffModes.Stream)), ActionPlacement.Menu));
            }
        }

        // Media stays where it is but answers to this PC: play, pause and skip from here.
        if (item.Playback is not null && !activity.Owner.IsPc)
        {
            actions.Add(Remote(activity, activity.IsPlaying ? "Pause" : "Play", activity.IsPlaying ? "" : "", MediaActions.Toggle));
            actions.Add(Remote(activity, "10 s", "", MediaActions.Skip, -10_000));
            actions.Add(Remote(activity, "10 s", "", MediaActions.Skip, 10_000));
        }

        _actionCache[key] = actions;
        return actions;
    }

    private ActivityAction Remote(ActivityViewModel activity, string label, string glyph, string action, long? positionMs = null) =>
        new(label, glyph, new RelayCommand(_ => _ = _coordinator.CommandAsync(
            new MediaCommandPayload(activity.Owner.DeviceId, activity.Id, action, positionMs, null))), ActionPlacement.Control);

    private void Refresh()
    {
        var devices = _coordinator.GetDevices();
        foreach (var device in devices)
        {
            if (device.Kind == DeviceKinds.Pc)
            {
                Local.Update(device);
                continue;
            }

            var phone = Phones.FirstOrDefault(item => item.DeviceId == device.DeviceId);
            if (phone is null)
            {
                phone = new DeviceViewModel(device.DeviceId, device.Kind);
                Phones.Add(phone);
            }

            phone.Update(device);
            phone.IsDefault = phone.DeviceId == _defaultPhoneId;
        }

        for (var index = Phones.Count - 1; index >= 0; index--)
        {
            if (devices.All(device => device.DeviceId != Phones[index].DeviceId))
            {
                Phones.RemoveAt(index);
            }
        }

        if (_actionCache.Count > 256)
        {
            _actionCache.Clear();
        }

        foreach (var activity in Local.Activities.Concat(Phones.SelectMany(phone => phone.Activities)))
        {
            activity.Actions = ActionsFor(activity);
        }

        Raise(nameof(HasPhones));
        Raise(nameof(HasOnlinePhone));
        Raise(nameof(PhoneSuggestion));
    }

    private void Tick()
    {
        foreach (var activity in Local.Activities.Concat(Phones.SelectMany(phone => phone.Activities)))
        {
            if (activity.IsPlaying)
            {
                activity.Tick();
            }
        }
    }
}
