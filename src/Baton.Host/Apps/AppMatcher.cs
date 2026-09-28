using System.Text.RegularExpressions;
using Baton.Host.Media;
using Baton.Protocol;

namespace Baton.Host.Apps;

/// <summary>
/// The ways an activity can continue on another device, best first: the content's usual app, then
/// the same app there (known pairs, Google Play Games titles, apps with the same name), each
/// browser for anything with a link, the app's website, and last a live stream of the source.
/// </summary>
public static partial class AppMatcher
{
    /// <summary>Android browsers Baton can hand a link to, in the order they are offered.</summary>
    private static readonly string[] AndroidBrowsers =
    [
        "com.android.chrome", "com.sec.android.app.sbrowser", "org.mozilla.firefox", "com.microsoft.emmx",
        "com.opera.browser", "com.opera.gx", "com.brave.browser", "com.vivaldi.browser", "com.chrome.beta"
    ];

    public static IReadOnlyList<HandoffChoice> Options(
        Activity activity,
        string sourcePlatform,
        string targetPlatform,
        IReadOnlyList<CatalogApp> targetApps,
        bool canStream)
    {
        var options = new List<HandoffChoice>();
        var known = KnownApps.CounterpartOf(sourcePlatform, activity.App.Id, activity.Window?.ProcessName);
        var provider = activity.Content?.Provider;
        var hasContent = activity.Url is not null
            || (activity.Kind is ActivityKind.AppMedia or ActivityKind.WebMedia && provider is not null and not "unknown")
            || (activity.Kind == ActivityKind.LocalMedia && activity.File is not null);

        // 1. The content itself, opened the way Baton always has: same video, same second.
        if (hasContent)
        {
            options.Add(new HandoffChoice(ChoiceKinds.Default, DefaultLabel(activity, targetPlatform, targetApps)));

            // Several builds of that app on the phone (YouTube ReVanced and RVX): each can be picked.
            var builds = Builds(activity, targetPlatform, targetApps);
            if (builds.Count > 1)
            {
                options.AddRange(builds.Select(build => new HandoffChoice(ChoiceKinds.App, build.Name, build.Id)));
            }
        }

        // 2. The same app on the other device. Media handed over as content already opens there.
        if (!hasContent || activity.Kind == ActivityKind.WebPage && known is not null && !known.IsBrowser)
        {
            foreach (var app in SameApp(activity, known, sourcePlatform, targetPlatform, targetApps))
            {
                options.Add(new HandoffChoice(ChoiceKinds.App, app.Name, app.Id));
            }
        }

        // 3. Anything with a link opens in whichever browser the user likes.
        if (activity.Url is not null)
        {
            foreach (var browser in Browsers(targetPlatform, targetApps))
            {
                options.Add(new HandoffChoice(ChoiceKinds.App, browser.Name, browser.Id));
            }
        }

        // 4. The app's website.
        if (activity.Url is null && known?.WebUrl is { } web)
        {
            options.Add(new HandoffChoice(ChoiceKinds.Web, $"{known.DisplayName} on the web", Url: web));
        }

        // 5. The screen itself, live.
        if (canStream)
        {
            options.Add(new HandoffChoice(ChoiceKinds.Stream, sourcePlatform == Platforms.Android ? "Mirror the phone" : "Stream the window"));
        }

        return options
            .GroupBy(option => (option.Kind, AppId: option.AppId?.ToLowerInvariant(), option.Url))
            .Select(group => group.First())
            .ToArray();
    }

    /// <summary>The installed builds of the content's own app on an Android target (original and patched ones).</summary>
    private static IReadOnlyList<CatalogApp> Builds(Activity activity, string targetPlatform, IReadOnlyList<CatalogApp> targetApps) =>
        targetPlatform == Platforms.Android && KnownApps.FromProvider(activity.Content?.Provider) is { } app
            ? app.AndroidPackages.Select(package => targetApps.FirstOrDefault(item => item.Id == package)).OfType<CatalogApp>().ToArray()
            : [];

    private static string DefaultLabel(Activity activity, string targetPlatform, IReadOnlyList<CatalogApp> targetApps) => activity switch
    {
        // The phone's own build of the app, e.g. "YouTube ReVanced"; with several, the phone opens the one in use.
        _ when Builds(activity, targetPlatform, targetApps) is { Count: > 0 } builds
            => builds.Count == 1 ? builds[0].Name : $"{KnownApps.FromProvider(activity.Content?.Provider)!.DisplayName} (the one you use)",
        { Kind: ActivityKind.LocalMedia } => targetPlatform == Platforms.Android ? "Baton player" : "Default player",
        { Kind: ActivityKind.WebPage } => "Default browser",
        _ when KnownApps.FromProvider(activity.Content?.Provider) is { } app => app.DisplayName,
        { Content.Provider: "web" } => "YouTube or the browser",
        _ => "Usual app"
    };

    /// <summary>The source app's counterpart(s) on the target device.</summary>
    private static IEnumerable<CatalogApp> SameApp(Activity activity, KnownApp? known, string sourcePlatform, string targetPlatform, IReadOnlyList<CatalogApp> targetApps)
    {
        var found = new List<CatalogApp>();
        var apps = targetApps.Where(app => !app.IsBrowser).ToArray();

        // A known pair.
        if (known is not null)
        {
            found.AddRange(targetPlatform == Platforms.Android
                ? apps.Where(app => string.Equals(app.Id, known.AndroidPackage, StringComparison.OrdinalIgnoreCase))
                : apps.Where(app => known.WindowsIdHints.Any(hint => app.Id.Contains(hint, StringComparison.OrdinalIgnoreCase)
                    || app.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))));
        }

        // A phone game that Google Play Games on PC installed: its entry carries the package.
        if (sourcePlatform == Platforms.Android)
        {
            found.AddRange(apps.Where(app => PlayGamesPackage(app.Id) is { } package
                && string.Equals(package, activity.App.Id, StringComparison.OrdinalIgnoreCase)));
        }
        else if (PlayGamesPackage(activity.App.Id) is { } package)
        {
            found.AddRange(apps.Where(app => string.Equals(app.Id, package, StringComparison.OrdinalIgnoreCase)));
        }

        // The same name: "Telegram" and "Telegram Desktop", "TikTok" and "TikTok".
        var name = Words(activity.App.Name);
        if (name.Length > 0 && string.Join(' ', name).Length >= 3)
        {
            found.AddRange(apps.Where(app => SameName(name, Words(app.Name))));
        }

        return found.DistinctBy(app => app.Id);
    }

    public static string? PlayGamesPackage(string id)
    {
        var match = PlayGamesId().Match(id);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static IEnumerable<CatalogApp> Browsers(string targetPlatform, IReadOnlyList<CatalogApp> targetApps) =>
        targetPlatform == Platforms.Windows
            ? targetApps.Where(app => app.IsBrowser)
            : AndroidBrowsers.Select(package => targetApps.FirstOrDefault(app => app.Id == package)).OfType<CatalogApp>();

    /// <summary>Equal, or one is the other plus words like "Desktop": never a mere substring.</summary>
    public static bool SameName(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return longer.Take(shorter.Length).SequenceEqual(shorter)
            && longer.Skip(shorter.Length).All(word => Filler.Contains(word));
    }

    private static readonly HashSet<string> Filler = ["desktop", "app", "for", "windows", "pc", "beta", "preview", "messenger", "lite", "uwp"];

    public static string[] Words(string name) =>
        WordPattern().Matches(name.ToLowerInvariant()).Select(match => match.Value).ToArray();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^googleplaygames://launch/\?id=([A-Za-z0-9_.]+)")]
    private static partial Regex PlayGamesId();
}
