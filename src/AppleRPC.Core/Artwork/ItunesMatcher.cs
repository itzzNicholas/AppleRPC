// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using System.Text.RegularExpressions;
using AppleRPC.Media;

namespace AppleRPC.Artwork;

/// <summary>One song returned by the iTunes Search API.</summary>
internal sealed record ItunesSong(
    string? TrackName, string? ArtistName, string? CollectionName,
    string? ArtworkUrl100, string? TrackViewUrl, string? CollectionViewUrl, string? ArtistViewUrl);

/// <summary>Parses iTunes Search API responses and picks the result that best matches title + artist + album.</summary>
internal static class ItunesMatcher
{
    private static readonly Regex ArtworkSize = new(@"/\d+x\d+(bb|-\d+)?\.(jpg|png|webp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<ItunesSong> Parse(string json)
    {
        var list = new List<ItunesSong>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var r in results.EnumerateArray())
        {
            list.Add(new ItunesSong(
                Str(r, "trackName"), Str(r, "artistName"), Str(r, "collectionName"),
                Str(r, "artworkUrl100"), Str(r, "trackViewUrl"), Str(r, "collectionViewUrl"), Str(r, "artistViewUrl")));
        }
        return list;
    }

    /// <summary>
    /// Scores every candidate (title weighs 3, artist 2, album 2) and returns the best one,
    /// or null if nothing has both a matching title and a matching artist.
    /// </summary>
    public static ItunesSong? PickBest(IEnumerable<ItunesSong> candidates, string title, string artist, string album)
    {
        ItunesSong? best = null;
        var bestScore = 0.0;
        var artistParts = TrackText.ArtistParts(artist);

        foreach (var c in candidates)
        {
            var titleScore = TrackText.Similarity(c.TrackName, title);
            var artistScore = ArtistScore(c.ArtistName, artist, artistParts);
            if (titleScore == 0 || artistScore == 0) continue;

            var albumScore = string.IsNullOrWhiteSpace(album) ? 0 : TrackText.Similarity(c.CollectionName, album);
            var score = 3 * titleScore + 2 * artistScore + 2 * albumScore;

            if (score > bestScore)   // strict ">" keeps iTunes' own relevance order on ties
            {
                best = c;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>"…/100x100bb.jpg" -> "…/600x600bb.jpg"</summary>
    public static string? UpscaleArtwork(string? url, int size = 600)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return ArtworkSize.IsMatch(url) ? ArtworkSize.Replace(url, $"/{size}x{size}bb.$2") : url;
    }

    private static double ArtistScore(string? candidate, string artist, IReadOnlyList<string> artistParts)
    {
        var full = TrackText.Similarity(candidate, artist);
        if (full > 0) return full;
        // "Daft Punk, Pharrell Williams" vs iTunes' "Daft Punk": try each credited artist.
        foreach (var part in artistParts)
        {
            var s = TrackText.Similarity(candidate, part);
            if (s > 0) return s * 0.9;
        }
        return 0;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
