using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Baton.Host.Handoff;
using Baton.Protocol;

namespace Baton.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>One continuable activity as a card shows it.</summary>
public sealed class ActivityViewModel : ObservableObject
{
    private Activity _activity;
    private double _progress;
    private string _timeText = string.Empty;

    public ActivityViewModel(Activity activity, DeviceViewModel owner)
    {
        _activity = activity;
        Owner = owner;
        Artwork = DecodeArtwork(activity.ArtworkJpegBase64);
        Tick();
    }

    public DeviceViewModel Owner { get; }
    public Activity Activity => _activity;
    public string Id => _activity.Id;
    public string Title => _activity.Title;
    public string Subtitle => _activity.Subtitle ?? string.Empty;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(_activity.Subtitle);
    public string AppName => _activity.App.Name;
    public BitmapImage? Artwork { get; private set; }
    public bool HasArtwork => Artwork is not null;
    public bool HasPlayback => _activity.Playback is { DurationMs: > 0 };
    public bool IsPlaying => _activity.Playback?.Playing == true;

    public string KindGlyph => _activity.Kind switch
    {
        ActivityKind.WebPage => "",
        ActivityKind.WebMedia => "",
        ActivityKind.AppMedia => _activity.Playback is { } ? "" : "",
        ActivityKind.LocalMedia => "",
        _ => ""
    };

    public string StateText => _activity.Kind == ActivityKind.WindowStream ? $"Window · {AppName}" : _activity.Playback switch
    {
        { Playing: true } => $"Playing in {AppName}",
        not null => $"Paused in {AppName}",
        _ => AppName
    };

    private IReadOnlyList<ActivityAction> _actions = [];

    /// <summary>What can be done with this activity from here: send it to a phone, or continue it on this PC.</summary>
    public IReadOnlyList<ActivityAction> Actions { get => _actions; set => Set(ref _actions, value); }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }

    public void Update(Activity activity)
    {
        var artworkChanged = activity.ArtworkJpegBase64 != _activity.ArtworkJpegBase64;
        _activity = activity;
        if (artworkChanged)
        {
            Artwork = DecodeArtwork(activity.ArtworkJpegBase64);
        }

        foreach (var name in new[] { nameof(Activity), nameof(Title), nameof(Subtitle), nameof(HasSubtitle), nameof(AppName),
                     nameof(Artwork), nameof(HasArtwork), nameof(HasPlayback), nameof(IsPlaying), nameof(KindGlyph), nameof(StateText) })
        {
            Raise(name);
        }

        Tick();
    }

    /// <summary>Advances the progress bar of something that is playing; called once a second.</summary>
    public void Tick()
    {
        if (_activity.Playback is not { DurationMs: > 0 } playback)
        {
            Progress = 0;
            TimeText = string.Empty;
            return;
        }

        var position = playback.PositionAt(DateTimeOffset.UtcNow);
        Progress = Math.Clamp((double)position / playback.DurationMs, 0, 1);
        TimeText = $"{Format(position)} / {Format(playback.DurationMs)}";
    }

    private static string Format(long ms)
    {
        var time = TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, Playback.MaxPlausibleMs));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private static BitmapImage? DecodeArtwork(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>A device card: this PC or a phone, with its activities.</summary>
public sealed class DeviceViewModel : ObservableObject
{
    private string _name = string.Empty;
    private bool _online;
    private PresenceState _presence;

    public DeviceViewModel(string deviceId, string kind)
    {
        DeviceId = deviceId;
        Kind = kind;
    }

    public string DeviceId { get; }
    public string Kind { get; }
    public bool IsPc => Kind == DeviceKinds.Pc;
    public string Glyph => IsPc ? "" : "";
    public ObservableCollection<ActivityViewModel> Activities { get; } = [];
    public ActivityViewModel? Top => Activities.FirstOrDefault();
    public bool HasActivity => Activities.Count > 0;

    public string Name { get => _name; private set => Set(ref _name, value); }

    public bool Online
    {
        get => _online;
        private set
        {
            if (Set(ref _online, value))
            {
                Raise(nameof(StatusText));
            }
        }
    }

    public string StatusText => !Online ? "Offline" : _presence switch
    {
        PresenceState.Locked => "Connected · locked",
        PresenceState.Idle => "Connected · idle",
        _ => "Connected"
    };

    public void Update(DeviceView view)
    {
        Name = view.Name;
        _presence = view.Presence;
        Online = view.Online;
        Raise(nameof(StatusText));

        // Reconcile by id so cards under the pointer are updated in place instead of rebuilt.
        var incoming = view.Activities.ToList();
        for (var index = Activities.Count - 1; index >= 0; index--)
        {
            if (incoming.All(activity => activity.Id != Activities[index].Id))
            {
                Activities.RemoveAt(index);
            }
        }

        for (var index = 0; index < incoming.Count; index++)
        {
            var existing = Activities.FirstOrDefault(item => item.Id == incoming[index].Id);
            if (existing is null)
            {
                Activities.Insert(index, new ActivityViewModel(incoming[index], this));
                continue;
            }

            existing.Update(incoming[index]);
            var current = Activities.IndexOf(existing);
            if (current != index)
            {
                Activities.Move(current, index);
            }
        }

        Raise(nameof(Top));
        Raise(nameof(HasActivity));
    }
}
