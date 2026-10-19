// SPDX-License-Identifier: AGPL-3.0-only
using AppleRPC.Media;

namespace AppleRPC.Tests;

public class TrackTextTests
{
    [Fact]
    public void Splits_the_apple_music_artist_em_dash_album_quirk()
    {
        var (artist, album) = TrackText.SplitArtistAlbum("Daft Punk — Random Access Memories", "");
        Assert.Equal("Daft Punk", artist);
        Assert.Equal("Random Access Memories", album);
    }

    [Fact]
    public void Leaves_a_proper_album_alone()
    {
        var (artist, album) = TrackText.SplitArtistAlbum("A — B", "Real Album");
        Assert.Equal("A — B", artist);
        Assert.Equal("Real Album", album);
    }

    [Theory]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("Get Lucky (feat. Pharrell Williams)", "get lucky")]
    [InlineData("Song - Remastered 2011", "song")]
    [InlineData("Album [Deluxe Edition]", "album")]
    [InlineData("AC/DC", "ac dc")]
    [InlineData("Simon & Garfunkel", "simon and garfunkel")]
    public void Normalize_makes_titles_comparable(string input, string expected) =>
        Assert.Equal(expected, TrackText.Normalize(input));

    [Fact]
    public void Similarity_scores_exact_contained_and_unrelated()
    {
        Assert.Equal(1.0, TrackText.Similarity("Daft Punk", "daft punk"));
        Assert.Equal(0.6, TrackText.Similarity("Daft Punk & Friends", "Daft Punk"));
        Assert.Equal(0, TrackText.Similarity("Daft Punk", "Justice"));
        Assert.Equal(0, TrackText.Similarity("", "Justice"));
    }

    [Fact]
    public void Artist_parts_and_primary_artist_handle_credits()
    {
        Assert.Equal(new[] { "daft punk", "pharrell williams" }, TrackText.ArtistParts("Daft Punk, Pharrell Williams"));
        Assert.Equal("Daft Punk", TrackText.PrimaryArtist("Daft Punk feat. Pharrell Williams"));
    }

    [Fact]
    public void Now_playing_position_advances_only_while_playing_and_is_clamped()
    {
        var t0 = DateTimeOffset.UtcNow;
        var playing = T.Track(posSec: 100, durSec: 200, at: t0);
        Assert.Equal(TimeSpan.FromSeconds(110), playing.PositionAt(t0.AddSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(200), playing.PositionAt(t0.AddSeconds(500)));

        var paused = T.Track(status: PlaybackStatus.Paused, posSec: 100, durSec: 200, at: t0);
        Assert.Equal(TimeSpan.FromSeconds(100), paused.PositionAt(t0.AddSeconds(10)));
    }
}
