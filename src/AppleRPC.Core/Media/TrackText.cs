// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AppleRPC.Media;

/// <summary>Text clean-up and fuzzy-comparison helpers for song metadata.</summary>
public static class TrackText
{
    private static readonly Regex Brackets = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex ArtistSplit = new(
        @"\s*(?:,|&|/|;|\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\bwith\b|\bx\b|\band\b)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The Apple Music app for Windows often reports <c>Artist = "Artist — Album"</c> and an empty AlbumTitle.
    /// When that happens, split it so the album ends up where it belongs.
    /// </summary>
    public static (string Artist, string Album) SplitArtistAlbum(string? artist, string? album)
    {
        artist = (artist ?? "").Trim();
        album = (album ?? "").Trim();
        if (album.Length == 0)
        {
            var idx = artist.IndexOf(" — ", StringComparison.Ordinal);
            if (idx > 0)
            {
                album = artist[(idx + 3)..].Trim();
                artist = artist[..idx].Trim();
            }
        }
        return (artist, album);
    }

    /// <summary>Lower-case, accent-free, bracket-free, suffix-free form used to compare titles/albums/artists.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Brackets.Replace(text, " ");
        var dash = s.IndexOf(" - ", StringComparison.Ordinal);   // "Song - Remastered 2011", "Album - Single"
        if (dash > 0) s = s[..dash];
        s = RemoveDiacritics(s).ToLowerInvariant();
        s = s.Replace("&", " and ");
        return NonAlnum.Replace(s, " ").Trim();
    }

    /// <summary>"Daft Punk, Pharrell Williams" -> ["daft punk", "pharrell williams"].</summary>
    public static IReadOnlyList<string> ArtistParts(string? artist) =>
        ArtistSplit.Split(artist ?? "").Select(Normalize).Where(p => p.Length > 0).Distinct().ToList();

    /// <summary>First credited artist, original casing ("A &amp; B" -&gt; "A").</summary>
    public static string PrimaryArtist(string? artist)
    {
        var first = ArtistSplit.Split(artist ?? "").Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0);
        return first ?? (artist ?? "").Trim();
    }

    /// <summary>1.0 = identical, 0.6 = one contains the other, 0 = unrelated.</summary>
    public static double Similarity(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1.0;
        if ((" " + x + " ").Contains(" " + y + " ") || (" " + y + " ").Contains(" " + x + " ")) return 0.6;
        return 0;
    }

    private static string RemoveDiacritics(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
