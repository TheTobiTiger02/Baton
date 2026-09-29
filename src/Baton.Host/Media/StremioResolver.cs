using System.Text.Json;
using System.Text.RegularExpressions;

namespace Baton.Host.Media;

/// <summary>
/// Finds what Stremio (or Harbor, a Stremio client) is playing, from the title its media session
/// shows: Cinemeta, Stremio's own catalog, gives the IMDb id. The result is a path under
/// <c>stremio:///detail/</c>: <c>series/tt0944947</c>, with <c>/tt0944947:1:3</c> when the title
/// names the episode. The other device opens it and Stremio resumes from the progress it syncs
/// with the account; Baton never picks or passes a stream.
/// </summary>
public sealed partial class StremioResolver(HttpClient http)
{
    private const string Catalog = "https://v3-cinemeta.strem.io/catalog";

    public async Task<string?> FindAsync(string title, long durationMs, CancellationToken cancellationToken)
    {
        var (name, season, episode) = ParseTitle(title);
        if (name.Length == 0)
        {
            return null;
        }

        // An episode is rarely longer than 75 minutes and a film rarely shorter: look there first.
        string[] types = season is not null || durationMs is > 0 and < 75 * 60_000 ? ["series", "movie"] : ["movie", "series"];
        foreach (var type in types)
        {
            var json = await http.GetStringAsync($"{Catalog}/{type}/top/search={Uri.EscapeDataString(name)}.json", cancellationToken);
            if (Match(json, name) is { } id)
            {
                return type == "series" && season is not null ? $"series/{id}/{id}:{season}:{episode}" : $"{type}/{id}";
            }
        }

        return null;
    }

    /// <summary>The IMDb id of the catalog entry named exactly <paramref name="name"/> (ignoring case and punctuation).</summary>
    public static string? Match(string catalogJson, string name)
    {
        using var document = JsonDocument.Parse(catalogJson);
        if (!document.RootElement.TryGetProperty("metas", out var metas) || metas.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var wanted = Normalize(name);
        foreach (var meta in metas.EnumerateArray())
        {
            if (meta.TryGetProperty("id", out var id) && id.GetString() is { } value && value.StartsWith("tt", StringComparison.Ordinal)
                && meta.TryGetProperty("name", out var metaName) && Normalize(metaName.GetString() ?? "") == wanted)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>"Lanterns - S01E03 - Title" → ("Lanterns", 1, 3); a plain title keeps no episode.</summary>
    public static (string Name, int? Season, int? Episode) ParseTitle(string title)
    {
        var match = EpisodePattern().Match(title);
        if (!match.Success)
        {
            return (title.Trim(), null, null);
        }

        var name = title[..match.Index].TrimEnd(' ', '-', '–', ':', '·', '|').Trim();
        var (season, episode) = match.Groups[1].Success ? (match.Groups[1], match.Groups[2]) : (match.Groups[3], match.Groups[4]);
        return (name, int.Parse(season.Value), int.Parse(episode.Value));
    }

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    [GeneratedRegex(@"\bS(\d{1,2})\s?E(\d{1,3})\b|\b(?:Season|Staffel)\s(\d{1,2})\D{1,12}(?:Episode|Folge)\s(\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePattern();
}
