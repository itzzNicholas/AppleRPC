// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using AppleRPC.Artwork;
using AppleRPC.Configuration;
using AppleRPC.Media;

namespace AppleRPC.Presence;

/// <summary>Turns "what's playing + what we found online" into the activity Discord will display. Pure function, easy to test.</summary>
public static class ActivityBuilder
{
    /// <summary>Discord limits: details/state/tooltips 128 bytes, image keys/URLs 256, link URLs 512.</summary>
    private const int MaxTextBytes = 128;
    private const int MaxImageLength = 256;
    private const int MaxUrlLength = 512;

    /// <summary>Album gets appended to the artist line only if the whole line stays short enough to fit on one row.</summary>
    private const int StateSoftLimit = 48;

    public static ActivityPayload Build(NowPlaying track, ArtworkInfo? art, Config config, DateTimeOffset now)
    {
        art ??= ArtworkInfo.Empty;
        var playing = track.Status == PlaybackStatus.Playing;

        return new ActivityPayload
        {
            Type = 2,
            StatusDisplayType = config.StatusDisplayType,
            Details = Fit(track.Title),
            DetailsUrl = ValidLink(art.TrackUrl),
            State = BuildState(track),
            StateUrl = ValidLink(art.ArtistUrl),
            Timestamps = playing ? BuildTimestamps(track, now) : null,   // paused: no timestamps -> no moving bar
            Assets = BuildAssets(track, art, config),
            Buttons = config.ShowGitHubButton
                ? new[] { new ActivityButton(Config.ButtonLabel, Config.GitHubUrl) }
                : null,
        };
    }

    /// <summary>start = now - position, end = start + duration. Discord draws the progress bar from these two.</summary>
    public static ActivityTimestamps BuildTimestamps(NowPlaying track, DateTimeOffset now)
    {
        var start = now - track.PositionAt(now);
        long? end = track.HasDuration ? (start + track.Duration).ToUnixTimeSeconds() : null;   // unknown length: elapsed counter only
        return new ActivityTimestamps(start.ToUnixTimeSeconds(), end);
    }

    private static string? BuildState(NowPlaying t)
    {
        if (string.IsNullOrWhiteSpace(t.Artist)) return string.IsNullOrWhiteSpace(t.Album) ? null : Fit(t.Album);
        if (!string.IsNullOrWhiteSpace(t.Album))
        {
            var combined = $"{t.Artist} • {t.Album}";
            if (combined.Length <= StateSoftLimit) return Fit(combined);
        }
        return Fit(t.Artist);
    }

    private static ActivityAssets BuildAssets(NowPlaying t, ArtworkInfo art, Config config)
    {
        var large = ValidImage(art.AlbumArtUrl) ?? config.DefaultAsset;
        // Artist photo -> album cover -> nothing (no point repeating the default logo twice).
        var small = ValidImage(art.ArtistImageUrl) ?? ValidImage(art.AlbumArtUrl);

        return new ActivityAssets
        {
            LargeImage = large,
            LargeText = Fit(string.IsNullOrWhiteSpace(t.Album) ? t.Title : t.Album),
            LargeUrl = ValidLink(art.AlbumUrl ?? art.TrackUrl),
            SmallImage = small,
            SmallText = small is null || string.IsNullOrWhiteSpace(t.Artist) ? null : Fit(t.Artist),
            SmallUrl = small is null ? null : ValidLink(art.ArtistUrl),
        };
    }

    /// <summary>Trims to 128 UTF-8 bytes (adding …) and pads to the 2-character minimum Discord requires.</summary>
    public static string Fit(string? text)
    {
        var s = (text ?? "").Trim();
        if (Encoding.UTF8.GetByteCount(s) > MaxTextBytes)
        {
            var sb = new StringBuilder();
            var bytes = Encoding.UTF8.GetByteCount("…");
            foreach (var rune in s.EnumerateRunes())
            {
                var n = rune.Utf8SequenceLength;
                if (bytes + n > MaxTextBytes) break;
                sb.Append(rune.ToString());
                bytes += n;
            }
            s = sb.ToString().TrimEnd() + "…";
        }
        return s.Length >= 2 ? s : s.PadRight(2, ' ');
    }

    private static string? ValidImage(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Length <= MaxImageLength && IsHttp(url) ? url : null;

    private static string? ValidLink(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Length <= MaxUrlLength && IsHttp(url) ? url : null;

    private static bool IsHttp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}
