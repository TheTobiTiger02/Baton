using Baton.Host.Handoff;
using Baton.Media;
using Baton.Protocol;

namespace Baton.Host.Media;

/// <summary>
/// The window the user worked in last, offered as the universal fallback: anything, even an app
/// Baton knows nothing about, can continue on the phone as a live stream of its window.
/// </summary>
public sealed class WindowSource : IActivitySource, IDisposable
{
    private readonly Timer _timer;
    private readonly LocalFiles _files;
    private Activity? _current;

    public WindowSource(LocalFiles files)
    {
        _files = files;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public event Action? Changed;

    public IReadOnlyList<Activity> Current =>
        _current is { Window: { } window } current && DesktopWindows.Exists(DesktopWindows.FromToken(window.WindowToken)) ? [current] : [];

    public Task<Activity?> TakeAsync(string activityId, bool pause, CancellationToken cancellationToken) =>
        Task.FromResult(Current.FirstOrDefault(activity => activity.Id == activityId));

    public static Activity ToActivity(WindowInfo window, DateTimeOffset seenAt) => new(
        $"window:{DesktopWindows.Token(window.Handle)}",
        string.Empty,
        ActivityKind.WindowStream,
        window.Title,
        new ActivityApp(window.ProcessName, window.ProcessName),
        seenAt,
        Subtitle: window.ProcessName,
        Window: new ActivityWindow(DesktopWindows.Token(window.Handle), window.ProcessName));

    private void Poll()
    {
        var window = DesktopWindows.Foreground();
        if (window is null)
        {
            return;
        }

        var token = DesktopWindows.Token(window.Handle);
        if (_current?.Window?.WindowToken == token && (_current.Title == window.Title || _current.Kind == ActivityKind.LocalMedia))
        {
            return;
        }

        _current = ToActivity(window, DateTimeOffset.UtcNow);

        // A player showing a local file (VLC, mpv, MPC...) hands over the file itself, which plays
        // better on the phone than a stream of the window.
        if (LocalFiles.IsKnownPlayer(window.ProcessName) && _files.FindPlaying(window.ProcessId, window.Title) is { } file)
        {
            var shared = _files.Share(file, MediaSessionSource.ProcessPath(window.ProcessId));
            _current = _current with
            {
                Id = $"file:{shared.FileId}",
                Kind = ActivityKind.LocalMedia,
                Title = Path.GetFileNameWithoutExtension(shared.Name),
                Subtitle = window.ProcessName,
                Content = new ActivityContent("file"),
                File = shared
            };
        }

        Changed?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
