using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baton.Host.Media;

/// <summary>
/// Finds a YouTube video id from what a media session shows: its title and channel. The YouTube
/// app on Android publishes no video id, so this is how a video watched there becomes a link on the
/// PC. Uses the same public search endpoint youtube.com's own page calls.
/// </summary>
public sealed class YouTubeResolver(HttpClient http, string? language = null)
{
    private const string SearchUrl = "https://www.youtube.com/youtubei/v1/search?prettyPrint=false";

    /// <summary>Shorts are at most three minutes long; their search results carry no length.</summary>
    public const long ShortsMaxMs = 180_000;

    /// <summary>
    /// YouTube translates titles for each viewer, so a title read on a German phone only matches a
    /// German search. This PC's language goes first; English catches titles left untranslated.
    /// </summary>
    public IReadOnlyList<string> Languages { get; } = SearchLanguages(language ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    public async Task<string?> FindVideoIdAsync(string title, string? channel, long durationMs, CancellationToken cancellationToken)
    {
        foreach (var hl in Languages)
        {
            if (Pick(await SearchAsync(title, channel, hl, cancellationToken), title, channel, durationMs) is { } id)
            {
                return id;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> SearchLanguages(string preferred) =>
        new[] { preferred.ToLowerInvariant(), "en" }
            .Where(code => code.Length == 2 && code != "iv")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private async Task<IReadOnlyList<Candidate>> SearchAsync(string title, string? channel, string hl, CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(channel) ? title : $"{title} {channel}";
        var body = new
        {
            context = new { client = new { clientName = "WEB", clientVersion = "2.20250901.00.00", hl } },
            query
        };

        using var response = await http.PostAsJsonAsync(SearchUrl, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Parse(JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)));
    }

    /// <summary>The first ten videos in result order, then the first ten Shorts.</summary>
    public static IReadOnlyList<Candidate> Parse(JsonNode? root)
    {
        var all = FindCandidates(root).ToArray();
        return all.Where(candidate => !candidate.IsShort).Take(10)
            .Concat(all.Where(candidate => candidate.IsShort).Take(10))
            .ToArray();
    }

    /// <summary>
    /// The exact title (and channel, when known) wins; otherwise the top result if its title is
    /// close. When the length of what was playing is known, a candidate of another length is never
    /// picked: a film's title also matches its trailer. A Short has no length in the results, so it
    /// only qualifies when what was playing is short enough to be one.
    /// </summary>
    public static string? Pick(IReadOnlyList<Candidate> all, string title, string? channel, long durationMs)
    {
        var candidates = durationMs > 0
            ? all.Where(candidate => SameLength(candidate.DurationMs, durationMs)
                || (candidate.IsShort && durationMs <= ShortsMaxMs + 1_000)).ToArray()
            : all;
        var wanted = Normalize(title);
        var exact = candidates.Where(candidate => Normalize(candidate.Title) == wanted).ToArray();
        if (exact.Length > 0)
        {
            var sameChannel = exact.Where(candidate => channel is not null && Normalize(candidate.Channel) == Normalize(channel));
            return sameChannel.Select(candidate => candidate.Id).FirstOrDefault() ?? exact[0].Id;
        }

        if (candidates.Count == 0 || wanted.Length == 0)
        {
            return null;
        }

        var top = Normalize(candidates[0].Title);
        return top.Length > 0 && (top.Contains(wanted) || wanted.Contains(top)) ? candidates[0].Id : null;
    }

    public sealed record Candidate(string Id, string Title, string Channel, long DurationMs, bool IsShort = false);

    public static bool SameLength(long candidateMs, long expectedMs) =>
        candidateMs > 0 && Math.Abs(candidateMs - expectedMs) <= Math.Max(3_000, expectedMs / 50);

    /// <summary>Reads "1:43:25" or "4:05" as milliseconds; 0 when absent (live streams).</summary>
    public static long ParseLength(string text)
    {
        long total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!long.TryParse(part, out var value))
            {
                return 0;
            }

            total = total * 60 + value;
        }

        return total * 1000;
    }

    private static IEnumerable<Candidate> FindCandidates(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["videoRenderer"] is JsonObject video && Text(video["videoId"]) is { } id)
                {
                    yield return new Candidate(id, Runs(video["title"]), Runs(video["ownerText"]), ParseLength(Runs(video["lengthText"])));
                }
                else if (obj["shortsLockupViewModel"] is JsonObject lockup
                    && Text(lockup["onTap"]?["innertubeCommand"]?["reelWatchEndpoint"]?["videoId"]) is { } shortId)
                {
                    yield return new Candidate(shortId, Text(lockup["overlayMetadata"]?["primaryText"]?["content"]) ?? string.Empty,
                        string.Empty, 0, IsShort: true);
                }
                else if (obj["reelItemRenderer"] is JsonObject reel && Text(reel["videoId"]) is { } reelId)
                {
                    yield return new Candidate(reelId, Runs(reel["headline"]), string.Empty, 0, IsShort: true);
                }

                foreach (var (_, child) in obj)
                {
                    foreach (var found in FindCandidates(child))
                    {
                        yield return found;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var found in FindCandidates(child))
                    {
                        yield return found;
                    }
                }

                break;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string Runs(JsonNode? text)
    {
        try
        {
            return text?["runs"]?[0]?["text"]?.GetValue<string>() ?? text?["simpleText"]?.GetValue<string>() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string Normalize(string value) =>
        new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
