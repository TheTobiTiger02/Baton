namespace Baton.Host.Media;

/// <summary>
/// The media apps Baton knows by name on both platforms. Windows identifies an app by AUMID or
/// executable, Android by package; <see cref="Provider"/> is the shared name that links the two.
/// </summary>
public sealed record KnownApp(
    string Provider,
    string DisplayName,
    string[] WindowsIdHints,
    string AndroidPackage,
    bool IsBrowser = false,
    bool IsVideoService = false,
    string? WebUrl = null,
    string? UriScheme = null,
    string[]? AndroidVariants = null)
{
    /// <summary>Every Android package that is this app: patched builds (ReVanced...) first, then the original.</summary>
    public IEnumerable<string> AndroidPackages => (AndroidVariants ?? []).Append(AndroidPackage);
}

public static class KnownApps
{
    public static readonly IReadOnlyList<KnownApp> All =
    [
        new("spotify", "Spotify", ["spotify"], "com.spotify.music", WebUrl: "https://open.spotify.com", UriScheme: "spotify"),
        new("netflix", "Netflix", ["netflix"], "com.netflix.mediaclient", IsVideoService: true, WebUrl: "https://www.netflix.com"),
        new("disney", "Disney+", ["disney"], "com.disney.disneyplus", IsVideoService: true),
        new("prime", "Prime Video", ["amazonvideo", "primevideo"], "com.amazon.avod.thirdpartyclient", IsVideoService: true),
        new("youtubemusic", "YouTube Music", ["youtube music", "youtubemusic"], "com.google.android.apps.youtube.music", WebUrl: "https://music.youtube.com",
            AndroidVariants: ["app.revanced.android.apps.youtube.music", "app.rvx.android.apps.youtube.music", "com.vanced.android.apps.youtube.music"]),
        new("youtube", "YouTube", ["youtube"], "com.google.android.youtube", WebUrl: "https://www.youtube.com",
            AndroidVariants: ["app.revanced.android.youtube", "app.rvx.android.youtube", "anddea.youtube", "com.vanced.android.youtube"]),
        new("vlc", "VLC", ["vlc"], "org.videolan.vlc"),
        new("chrome", "Chrome", ["chrome"], "com.android.chrome", IsBrowser: true),
        new("edge", "Edge", ["msedge", "microsoft.microsoftedge"], "com.microsoft.emmx", IsBrowser: true),
        new("opera", "Opera", ["opera"], "com.opera.browser", IsBrowser: true),
        new("firefox", "Firefox", ["firefox"], "org.mozilla.firefox", IsBrowser: true),
        new("brave", "Brave", ["brave"], "com.brave.browser", IsBrowser: true),
        new("vivaldi", "Vivaldi", ["vivaldi"], "com.vivaldi.browser", IsBrowser: true),
        new("zen", "Zen", ["zen"], "org.mozilla.firefox", IsBrowser: true),
        new("librewolf", "LibreWolf", ["librewolf"], "org.mozilla.firefox", IsBrowser: true),
        new("floorp", "Floorp", ["floorp"], "org.mozilla.firefox", IsBrowser: true),
        new("arc", "Arc", ["arc.exe", "thebrowsercompany"], "com.android.chrome", IsBrowser: true)
    ];

    /// <summary>
    /// Apps that exist on both platforms (or on the web) but are not media players: the same app is
    /// what continues there. Matched only when choosing the app a handoff continues in.
    /// </summary>
    public static readonly IReadOnlyList<KnownApp> Counterparts =
    [
        new("whatsapp", "WhatsApp", ["whatsapp"], "com.whatsapp", WebUrl: "https://web.whatsapp.com", UriScheme: "whatsapp"),
        new("telegram", "Telegram", ["telegram"], "org.telegram.messenger", WebUrl: "https://web.telegram.org", UriScheme: "tg"),
        new("discord", "Discord", ["discord"], "com.discord", WebUrl: "https://discord.com/app", UriScheme: "discord"),
        new("tiktok", "TikTok", ["tiktok"], "com.zhiliaoapp.musically", WebUrl: "https://www.tiktok.com"),
        new("instagram", "Instagram", ["instagram"], "com.instagram.android", WebUrl: "https://www.instagram.com"),
        new("facebook", "Facebook", ["facebook"], "com.facebook.katana", WebUrl: "https://www.facebook.com"),
        new("x", "X", ["twitter"], "com.twitter.android", WebUrl: "https://x.com"),
        new("reddit", "Reddit", ["reddit"], "com.reddit.frontpage", WebUrl: "https://www.reddit.com"),
        new("todo", "Microsoft To Do", ["microsoft.todos"], "com.microsoft.todos", WebUrl: "https://to-do.office.com"),
        new("outlook", "Outlook", ["microsoft.outlookforwindows", "outlook.exe"], "com.microsoft.office.outlook", WebUrl: "https://outlook.live.com"),
        new("teams", "Teams", ["msteams"], "com.microsoft.teams", WebUrl: "https://teams.microsoft.com"),
        new("samsungnotes", "Samsung Notes", ["samsungnotes"], "com.samsung.android.app.notes"),
        new("keepass", "KeePassXC", ["keepassxc"], "keepass2android.keepass2android"),
        new("gmail", "Gmail", [], "com.google.android.gm", WebUrl: "https://mail.google.com"),
        new("gcalendar", "Google Calendar", [], "com.google.android.calendar", WebUrl: "https://calendar.google.com"),
        new("gphotos", "Google Photos", [], "com.google.android.apps.photos", WebUrl: "https://photos.google.com"),
        new("gdrive", "Google Drive", [], "com.google.android.apps.docs", WebUrl: "https://drive.google.com"),
        new("gmaps", "Google Maps", [], "com.google.android.apps.maps", WebUrl: "https://maps.google.com"),
        new("gfit", "Google Fit", [], "com.google.android.apps.fitness"),
        new("revolut", "Revolut", [], "com.revolut.revolut", WebUrl: "https://app.revolut.com"),
        new("chatgpt", "ChatGPT", ["chatgpt"], "com.openai.chatgpt", WebUrl: "https://chatgpt.com"),
        new("claude", "Claude", ["claude"], "com.anthropic.claude", WebUrl: "https://claude.ai")
    ];

    /// <summary>The known counterpart (media or not) of an app on either platform.</summary>
    public static KnownApp? CounterpartOf(string platform, string? appId, string? processName = null)
    {
        if (platform == Baton.Protocol.Platforms.Android)
        {
            return Counterparts.Concat(All).FirstOrDefault(app => app.AndroidPackages.Contains(appId, StringComparer.OrdinalIgnoreCase))
                ?? FromAndroidPackage(appId);
        }

        foreach (var id in new[] { appId, processName })
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var lower = id.ToLowerInvariant();
            if (Counterparts.Concat(All).FirstOrDefault(app => app.WindowsIdHints.Any(lower.Contains)) is { } known)
            {
                return known;
            }
        }

        return null;
    }

    public static KnownApp? FromWindowsId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var lower = id.ToLowerInvariant();
        return All.FirstOrDefault(app => app.WindowsIdHints.Any(lower.Contains));
    }

    public static KnownApp? FromProvider(string? provider) =>
        All.FirstOrDefault(app => string.Equals(app.Provider, provider, StringComparison.OrdinalIgnoreCase));

    public static KnownApp? FromAndroidPackage(string? package) =>
        All.FirstOrDefault(app => app.AndroidPackages.Contains(package, StringComparer.OrdinalIgnoreCase))
        ?? (package?.Contains("youtube", StringComparison.OrdinalIgnoreCase) == true
            ? FromProvider(package.Contains("music", StringComparison.OrdinalIgnoreCase) ? "youtubemusic" : "youtube")
            : null);

    /// <summary>A readable app name for an AUMID like <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c> or <c>chrome.exe</c>.</summary>
    public static string DisplayName(string id)
    {
        if (FromWindowsId(id) is { } known)
        {
            return known.DisplayName;
        }

        var name = id.Split('!')[^1];
        name = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        name = name.Split('.')[^1];
        return string.IsNullOrWhiteSpace(name) ? id : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
