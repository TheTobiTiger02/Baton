using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using Baton.Host.Handoff;
using Baton.Protocol;

namespace Baton.Host.Media;

/// <summary>
/// Continues web pages, web media and media apps on this PC: builds the best link it can for the
/// content, opens it with the default handler, then nudges the resulting media session to the
/// right second.
/// </summary>
public sealed class MediaOpener(
    MediaSessionSource sessions,
    YouTubeResolver youtube,
    DiagnosticsLog diagnostics,
    Action<string, long>? expectBrowserSeek = null,
    Func<Activity, long, Task<bool>>? resumeExisting = null) : IActivityOpener, IPositionReconciler
{
    public async Task<bool> ReconcileAsync(Activity activity, long positionMs, CancellationToken cancellationToken)
    {
        if (activity.Kind is not (ActivityKind.WebPage or ActivityKind.WebMedia or ActivityKind.AppMedia))
        {
            return false;
        }

        // A browser tab is corrected through the extension: browsers' system media controls
        // (Zen's, Firefox's) often can't seek.
        if (resumeExisting is not null && await resumeExisting(activity, positionMs))
        {
            return true;
        }

        var provider = activity.Content?.Provider;
        return await sessions.ReconcileAsync(
            candidate => TitlesMatch(candidate.Title, activity.Title)
                || (provider is not null and not "web" and not "unknown" && KnownApps.FromWindowsId(candidate.App.Id)?.Provider == provider),
            positionMs,
            play: true,
            TimeSpan.FromSeconds(15),
            toleranceMs: 1_500);
    }

    private static readonly TimeSpan ReconcileTimeout = TimeSpan.FromSeconds(25);

    public Task<OpenResult?> TryOpenAsync(Activity activity, CancellationToken cancellationToken) =>
        TryOpenAsync(activity, browserExe: null, cancellationToken);

    /// <summary>
    /// Opens the activity at its position; web links go to <paramref name="browserExe"/> when one
    /// was picked for it, else to the default handler.
    /// </summary>
    public async Task<OpenResult?> TryOpenAsync(Activity activity, string? browserExe, CancellationToken cancellationToken)
    {
        if (activity.Kind is not (ActivityKind.WebPage or ActivityKind.WebMedia or ActivityKind.AppMedia))
        {
            return null;
        }

        var positionMs = activity.Playback?.PositionAt(DateTimeOffset.UtcNow) ?? 0;

        // Still open here (the tab it was sent from)? Continue there before looking anything up.
        if (activity.Playback is not null && resumeExisting is not null && await resumeExisting(activity, positionMs))
        {
            return OpenResult.Opened("Continued where it was on this PC");
        }

        var (target, result) = await ChooseTargetAsync(activity, positionMs, cancellationToken);
        if (target is null)
        {
            return result;
        }

        // Back to where it came from: the tab (or app) that still has it continues, no new tab.
        if (activity.Playback is not null && resumeExisting is not null
            && await resumeExisting(activity with { Url = target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? target : activity.Url }, positionMs))
        {
            return OpenResult.Opened("Continued where it was on this PC");
        }

        diagnostics.Record(DiagnosticsCategory.Handoff, "open.link", target);
        if (activity.Playback is not null && target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // The extension seeks the page as soon as its player loads: sooner than the media
            // session appears, and it works for sites whose links carry no timestamp.
            expectBrowserSeek?.Invoke(target, positionMs);
        }

        Process.Start(browserExe is not null && target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo(browserExe) { UseShellExecute = false, ArgumentList = { target } }
            : new ProcessStartInfo(target) { UseShellExecute = true });

        if (activity.Playback is not null && result.Status == HandoffStatus.Opened)
        {
            var provider = activity.Content?.Provider;
            _ = sessions.ReconcileAsync(
                candidate => TitlesMatch(candidate.Title, activity.Title)
                    || (provider is not null and not "web" && KnownApps.FromWindowsId(candidate.App.Id)?.Provider == provider),
                positionMs,
                play: true,
                ReconcileTimeout);
        }

        return result;
    }

    private async Task<(string? Target, OpenResult Result)> ChooseTargetAsync(Activity activity, long positionMs, CancellationToken cancellationToken)
    {
        if (activity.Url is { } url)
        {
            return (ContentLinks.YouTubeVideoId(url) is not null ? ContentLinks.WithYouTubeTime(url, positionMs) : url, OpenResult.Opened());
        }

        var content = activity.Content;
        var query = content?.Query ?? string.Join(' ', new[] { activity.Title, activity.Subtitle }.Where(part => part is not null));
        switch (content?.Provider)
        {
            case "web" or "unknown":
            {
                // Only the title is known. Trust a YouTube match only when its length agrees too.
                string? id = null;
                try
                {
                    id = await youtube.FindVideoIdAsync(activity.Title, activity.Subtitle, activity.Playback?.DurationMs ?? 0, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    diagnostics.Record(DiagnosticsCategory.Handoff, "youtube.search.failed", ex.Message, severity: DiagnosticsSeverity.Warning);
                }

                return id is not null
                    ? (ContentLinks.YouTubeWatch(id, positionMs), OpenResult.Opened())
                    : (null, OpenResult.Failed(content.Provider == "web"
                        ? "Baton couldn't tell which page this is. Share the page to Baton on the phone to send its exact link."
                        : $"Baton can't find {activity.Title} on this PC. {activity.App.Name} isn't available here."));
            }

            case "youtube" or "youtubemusic":
            {
                var music = content.Provider == "youtubemusic";
                var id = content.Id;
                if (id is null)
                {
                    try
                    {
                        id = await youtube.FindVideoIdAsync(activity.Title, activity.Subtitle, activity.Playback?.DurationMs ?? 0, cancellationToken);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                    {
                        diagnostics.Record(DiagnosticsCategory.Handoff, "youtube.search.failed", ex.Message, severity: DiagnosticsSeverity.Warning);
                    }
                }

                return id is not null
                    ? (ContentLinks.YouTubeWatch(id, positionMs, music), OpenResult.Opened())
                    : (ContentLinks.SearchUrl(content.Provider, query), OpenResult.Fallback("Couldn't find the exact video, so Baton opened a search."));
            }

            case "spotify":
                return content.Id?.StartsWith("spotify:", StringComparison.Ordinal) == true
                    ? (content.Id, OpenResult.Opened())
                    : (ContentLinks.SearchUrl("spotify", query), OpenResult.Fallback("Opened a Spotify search for the track."));

            case "netflix" or "disney" or "prime":
            {
                var name = KnownApps.FromProvider(content.Provider)!.DisplayName;
                return (ContentLinks.SearchUrl(content.Provider, activity.Title),
                    OpenResult.Fallback($"Opened {activity.Title} in {name}. Press Resume: {name} remembers where you stopped."));
            }

            default:
                return (null, OpenResult.Failed($"This PC has no app for {activity.App.Name}."));
        }
    }

    public static bool TitlesMatch(string a, string b)
    {
        static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        var left = Normalize(a);
        var right = Normalize(b);
        return left.Length > 0 && right.Length > 0 && (left.Contains(right) || right.Contains(left));
    }
}
