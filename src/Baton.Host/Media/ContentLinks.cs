using System.Text.RegularExpressions;

namespace Baton.Host.Media;

/// <summary>Building and reading the links of services Baton understands.</summary>
public static partial class ContentLinks
{
    /// <summary>A YouTube watch link that starts at <paramref name="positionMs"/>.</summary>
    public static string YouTubeWatch(string videoId, long positionMs, bool music = false)
    {
        var host = music ? "music.youtube.com" : "www.youtube.com";
        var seconds = positionMs / 1000;
        return seconds > 0
            ? $"https://{host}/watch?v={Uri.EscapeDataString(videoId)}&t={seconds}s"
            : $"https://{host}/watch?v={Uri.EscapeDataString(videoId)}";
    }

    public static string? YouTubeVideoId(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host == "youtu.be")
        {
            return uri.AbsolutePath.Trim('/').Split('/')[0] is { Length: > 0 } shortId ? shortId : null;
        }

        if (!host.EndsWith("youtube.com"))
        {
            return null;
        }

        var match = VideoIdQuery().Match(uri.Query);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        var path = PathVideoId().Match(uri.AbsolutePath);
        return path.Success ? path.Groups[1].Value : null;
    }

    /// <summary>Replaces or adds the <c>t</c> parameter of a YouTube link.</summary>
    public static string WithYouTubeTime(string url, long positionMs)
    {
        var id = YouTubeVideoId(url);
        if (id is null)
        {
            return url;
        }

        return YouTubeWatch(id, positionMs, url.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where to look for <paramref name="query"/> on a service when the exact item is unknown.</summary>
    /// <summary>
    /// The page scrolled to the text a browser should show at the top: a text fragment
    /// (<c>#:~:text=</c>), which Chrome, Edge, Samsung Internet and Firefox 131+ scroll to by
    /// themselves. The page's own <c>#fragment</c> stays in front.
    /// </summary>
    public static string WithTextAnchor(string url, string? anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor) || url.Contains(":~:", StringComparison.Ordinal))
        {
            return url;
        }

        // The directive's own separators must be escaped inside the text.
        var text = Uri.EscapeDataString(anchor.Trim()).Replace("-", "%2D").Replace(",", "%2C").Replace("&", "%26");
        return url.Contains('#') ? $"{url}:~:text={text}" : $"{url}#:~:text={text}";
    }

    /// <summary>The Stremio page of a title (see <see cref="StremioResolver"/>): Stremio and Harbor both open it.</summary>
    public static string StremioDetail(string id) => $"stremio:///detail/{id}";

    /// <summary>
    /// A web link to a chat as the messenger's own app takes it: <c>wa.me/49151…?text=…</c> becomes
    /// <c>whatsapp://send?phone=49151…&amp;text=…</c>, <c>t.me/name</c> becomes <c>tg://resolve?domain=name</c>.
    /// Null for anything else, or another app's <paramref name="scheme"/>.
    /// </summary>
    public static string? ForApp(string scheme, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var path = uri.AbsolutePath.Trim('/');
        var text = System.Web.HttpUtility.ParseQueryString(uri.Query)["text"];
        switch (scheme.ToLowerInvariant(), uri.Host.ToLowerInvariant())
        {
            case ("whatsapp", "wa.me") when path.Length is >= 7 and <= 15 && path.All(char.IsAsciiDigit):
                return $"whatsapp://send?phone={path}" + (text is { Length: > 0 } ? "&text=" + Uri.EscapeDataString(text) : "");
            case ("tg", "t.me") when TelegramName().IsMatch(path):
                return $"tg://resolve?domain={path}";
            default:
                return null;
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{3,31}$")]
    private static partial Regex TelegramName();

    /// <summary>A Twitch channel's page, when the name is one (letters, digits, underscore).</summary>
    public static string? TwitchChannel(string? name) =>
        name is { Length: >= 3 and <= 25 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
        && !TwitchPages.Contains(name.ToLowerInvariant()) ? $"https://www.twitch.tv/{name.ToLowerInvariant()}" : null;

    private static readonly HashSet<string> TwitchPages = ["directory", "search", "videos", "settings", "downloads", "subscriptions", "inventory", "login", "signup", "wallet", "drops"];

    public static string? TwitchUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (uri.Scheme == "twitch")
            return uri.Host switch
            {
                "stream" or "channel" when parts.Length == 1 => TwitchChannel(parts[0]),
                "video" or "videos" when parts.Length == 1 && parts[0].Length > 0 && parts[0].All(char.IsAsciiDigit) => $"https://www.twitch.tv/videos/{parts[0]}",
                _ => null
            };
        if (uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort || uri.Host is not ("twitch.tv" or "www.twitch.tv" or "m.twitch.tv")) return null;
        if (parts.Length == 1) return TwitchChannel(parts[0]);
        return parts.Length == 2 && parts[0] == "videos" && parts[1].Length > 0 && parts[1].All(char.IsAsciiDigit)
            ? $"https://www.twitch.tv/videos/{parts[1]}" : null;
    }

    public static bool IsTwitchLive(string url) => TwitchUrl(url) is { } target && !new Uri(target).AbsolutePath.StartsWith("/videos/", StringComparison.Ordinal);

    public static string WithTwitchTime(string url, long positionMs)
    {
        var target = TwitchUrl(url);
        if (target is null || !new Uri(target).AbsolutePath.StartsWith("/videos/", StringComparison.Ordinal)) return url;
        var seconds = Math.Max(0, positionMs / 1000);
        return $"{target}?t={seconds / 3600}h{seconds / 60 % 60}m{seconds % 60}s";
    }

    public static string? SearchUrl(string provider, string query)
    {
        var q = Uri.EscapeDataString(query);
        return provider switch
        {
            "youtube" => $"https://www.youtube.com/results?search_query={q}",
            "youtubemusic" => $"https://music.youtube.com/search?q={q}",
            "spotify" => $"spotify:search:{q}",
            "netflix" => $"https://www.netflix.com/search?q={q}",
            "disney" => $"https://www.disneyplus.com/search?q={q}",
            "prime" => $"https://www.primevideo.com/search/ref=atv_nb_sug?phrase={q}",
            "twitch" => $"https://www.twitch.tv/search?term={q}",
            "stremio" => $"stremio:///search?search={q}",
            _ => null
        };
    }

    [GeneratedRegex(@"[?&]v=([A-Za-z0-9_-]{6,})")]
    private static partial Regex VideoIdQuery();

    [GeneratedRegex(@"^/(?:shorts|live|embed)/([A-Za-z0-9_-]{6,})")]
    private static partial Regex PathVideoId();
}
