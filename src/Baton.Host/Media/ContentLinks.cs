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
            _ => null
        };
    }

    [GeneratedRegex(@"[?&]v=([A-Za-z0-9_-]{6,})")]
    private static partial Regex VideoIdQuery();

    [GeneratedRegex(@"^/(?:shorts|live|embed)/([A-Za-z0-9_-]{6,})")]
    private static partial Regex PathVideoId();
}
