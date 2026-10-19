// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using System.Text.Json;
using AppleRPC.Artwork;
using AppleRPC.Configuration;
using AppleRPC.Media;
using AppleRPC.Presence;

namespace AppleRPC.Tests;

public class ActivityBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly ArtworkInfo FullArt = new(
        AlbumArtUrl: "https://is1-ssl.mzstatic.com/600x600bb.jpg",
        ArtistImageUrl: "https://cdn-images.dzcdn.net/artist.jpg",
        TrackUrl: "https://music.apple.com/track",
        AlbumUrl: "https://music.apple.com/album",
        ArtistUrl: "https://music.apple.com/artist");

    [Fact]
    public void Playing_track_becomes_a_listening_activity_with_a_progress_bar()
    {
        var track = T.Track(posSec: 60, durSec: 300, at: Now);

        var a = ActivityBuilder.Build(track, FullArt, T.Config(), Now);

        Assert.Equal(2, a.Type);                        // Listening
        Assert.Equal(2, a.StatusDisplayType);           // status shows the song title
        Assert.Equal("Get Lucky", a.Details);
        Assert.Equal("Daft Punk • Random Access Memories", a.State);
        Assert.Equal(Now.ToUnixTimeSeconds() - 60, a.Timestamps!.Start);
        Assert.Equal(Now.ToUnixTimeSeconds() - 60 + 300, a.Timestamps.End);
    }

    [Fact]
    public void Links_and_images_are_filled_in()
    {
        var a = ActivityBuilder.Build(T.Track(at: Now), FullArt, T.Config(), Now);

        Assert.Equal("https://music.apple.com/track", a.DetailsUrl);
        Assert.Equal("https://music.apple.com/artist", a.StateUrl);
        Assert.Equal(FullArt.AlbumArtUrl, a.Assets!.LargeImage);
        Assert.Equal("Random Access Memories", a.Assets.LargeText);          // tooltip = album
        Assert.Equal("https://music.apple.com/album", a.Assets.LargeUrl);
        Assert.Equal(FullArt.ArtistImageUrl, a.Assets.SmallImage);
        Assert.Equal("Daft Punk", a.Assets.SmallText);                        // tooltip = artist
        Assert.Equal("https://music.apple.com/artist", a.Assets.SmallUrl);
    }

    [Fact]
    public void Paused_track_has_no_timestamps()
    {
        var a = ActivityBuilder.Build(T.Track(status: PlaybackStatus.Paused, at: Now), FullArt, T.Config(), Now);
        Assert.Null(a.Timestamps);
        Assert.Equal("Get Lucky", a.Details);
    }

    [Fact]
    public void Unknown_duration_shows_only_elapsed_time()
    {
        var a = ActivityBuilder.Build(T.Track(posSec: 10, durSec: 0, at: Now), FullArt, T.Config(), Now);
        Assert.NotNull(a.Timestamps!.Start);
        Assert.Null(a.Timestamps.End);
    }

    [Fact]
    public void Timestamps_follow_the_extrapolated_position_after_a_seek()
    {
        var sampled = Now.AddSeconds(-10);
        var track = T.Track(posSec: 100, durSec: 300, at: sampled);   // sampled 10 s ago at 1:40 -> now at 1:50

        var ts = ActivityBuilder.BuildTimestamps(track, Now);

        Assert.Equal(Now.ToUnixTimeSeconds() - 110, ts.Start);
        Assert.Equal(Now.ToUnixTimeSeconds() - 110 + 300, ts.End);
    }

    [Fact]
    public void Without_artwork_falls_back_to_the_default_asset_and_hides_the_small_image()
    {
        var a = ActivityBuilder.Build(T.Track(at: Now), ArtworkInfo.Empty, T.Config(), Now);

        Assert.Equal("applerpc", a.Assets!.LargeImage);
        Assert.Null(a.Assets.SmallImage);
        Assert.Null(a.Assets.SmallText);
        Assert.Null(a.DetailsUrl);
    }

    [Fact]
    public void Missing_artist_photo_falls_back_to_the_album_cover()
    {
        var art = FullArt with { ArtistImageUrl = null };
        var a = ActivityBuilder.Build(T.Track(at: Now), art, T.Config(), Now);
        Assert.Equal(FullArt.AlbumArtUrl, a.Assets!.SmallImage);
    }

    [Fact]
    public void Github_button_is_present_by_default_and_can_be_disabled()
    {
        var on = ActivityBuilder.Build(T.Track(at: Now), null, T.Config(), Now);
        var button = Assert.Single(on.Buttons!);
        Assert.Equal("⭐ Star on GitHub", button.Label);
        Assert.Equal("https://github.com/itzzNicholas/AppleRPC", button.Url);

        var off = ActivityBuilder.Build(T.Track(at: Now), null, T.Config(c => c.ShowGitHubButton = false), Now);
        Assert.Null(off.Buttons);
    }

    [Fact]
    public void Long_album_names_stay_out_of_the_state_line()
    {
        var a = ActivityBuilder.Build(
            T.Track(album: "An Extremely Long Album Title That Would Never Fit On One Line"), null, T.Config(), Now);
        Assert.Equal("Daft Punk", a.State);
    }

    [Fact]
    public void Text_is_trimmed_to_128_bytes_and_padded_to_2_chars()
    {
        var long8 = new string('é', 200);   // 2 bytes each
        var fitted = ActivityBuilder.Fit(long8);
        Assert.True(Encoding.UTF8.GetByteCount(fitted) <= 128);
        Assert.EndsWith("…", fitted);

        Assert.Equal(2, ActivityBuilder.Fit("A").Length);
        Assert.Equal(2, ActivityBuilder.Fit("").Length);
        Assert.Equal("Get Lucky", ActivityBuilder.Fit("  Get Lucky "));
    }

    [Fact]
    public void Invalid_or_oversized_urls_are_dropped()
    {
        var art = new ArtworkInfo(
            AlbumArtUrl: "javascript:alert(1)",
            ArtistImageUrl: "https://example.com/" + new string('a', 300),
            TrackUrl: "not a url");

        var a = ActivityBuilder.Build(T.Track(at: Now), art, T.Config(), Now);

        Assert.Equal("applerpc", a.Assets!.LargeImage);
        Assert.Null(a.Assets.SmallImage);
        Assert.Null(a.DetailsUrl);
    }

    [Fact]
    public void Serializes_with_discords_snake_case_field_names_and_omits_nulls()
    {
        var a = ActivityBuilder.Build(T.Track(at: Now), FullArt, T.Config(), Now);
        var json = JsonSerializer.Serialize(a, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        Assert.Equal(2, r.GetProperty("type").GetInt32());
        Assert.Equal(2, r.GetProperty("status_display_type").GetInt32());
        Assert.Equal("Get Lucky", r.GetProperty("details").GetString());
        Assert.True(r.TryGetProperty("details_url", out _));
        Assert.True(r.TryGetProperty("state_url", out _));
        Assert.True(r.GetProperty("assets").TryGetProperty("large_url", out _));
        Assert.True(r.GetProperty("assets").TryGetProperty("small_url", out _));
        Assert.Equal("⭐ Star on GitHub", r.GetProperty("buttons")[0].GetProperty("label").GetString());

        var paused = JsonSerializer.Serialize(
            ActivityBuilder.Build(T.Track(status: PlaybackStatus.Paused), null, T.Config(), Now),
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Assert.DoesNotContain("timestamps", paused);
        Assert.DoesNotContain("details_url", paused);
    }

    [Fact]
    public void Equivalence_ignores_timestamp_jitter_but_notices_seeks_and_track_changes()
    {
        var a = ActivityBuilder.Build(T.Track(posSec: 60, at: Now), FullArt, T.Config(), Now);
        var jitter = ActivityBuilder.Build(T.Track(posSec: 60, at: Now.AddMilliseconds(900)), FullArt, T.Config(), Now.AddMilliseconds(900));
        var seeked = ActivityBuilder.Build(T.Track(posSec: 200, at: Now), FullArt, T.Config(), Now);
        var other = ActivityBuilder.Build(T.Track(title: "Instant Crush", at: Now), FullArt, T.Config(), Now);

        Assert.True(a.IsEquivalentTo(jitter));
        Assert.False(a.IsEquivalentTo(seeked));
        Assert.False(a.IsEquivalentTo(other));
        Assert.False(a.IsEquivalentTo(null));
    }
}
