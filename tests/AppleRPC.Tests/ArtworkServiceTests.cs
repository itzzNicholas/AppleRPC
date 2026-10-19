// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using AppleRPC.Artwork;
using AppleRPC.Configuration;

namespace AppleRPC.Tests;

public class ItunesMatcherTests
{
    [Fact]
    public void Picks_the_result_matching_title_artist_and_album_over_covers_and_singles()
    {
        var songs = ItunesMatcher.Parse(T.ItunesGetLucky);

        var best = ItunesMatcher.PickBest(songs, "Get Lucky", "Daft Punk", "Random Access Memories");

        Assert.NotNull(best);
        Assert.Equal("Random Access Memories", best!.CollectionName);   // album match beats the "Radio Edit - Single"
    }

    [Fact]
    public void Rejects_results_by_a_different_artist()
    {
        var songs = ItunesMatcher.Parse(T.ItunesGetLucky);
        Assert.Null(ItunesMatcher.PickBest(songs, "Get Lucky", "Justice", "Cross"));
    }

    [Fact]
    public void Accepts_one_of_several_credited_artists()
    {
        var songs = ItunesMatcher.Parse(T.ItunesGetLucky);
        var best = ItunesMatcher.PickBest(songs, "Get Lucky", "Pharrell Williams, Daft Punk", "");
        Assert.NotNull(best);
        Assert.Equal("Daft Punk", best!.ArtistName);
    }

    [Theory]
    [InlineData("https://x/y/100x100bb.jpg", "https://x/y/600x600bb.jpg")]
    [InlineData("https://x/y/cover/100x100bb.png", "https://x/y/cover/600x600bb.png")]
    [InlineData("https://x/y/nosize.jpg", "https://x/y/nosize.jpg")]
    public void Upscales_the_artwork_url(string input, string expected) =>
        Assert.Equal(expected, ItunesMatcher.UpscaleArtwork(input));

    [Fact]
    public void Parse_tolerates_garbage_shapes()
    {
        Assert.Empty(ItunesMatcher.Parse("""{"resultCount":0}"""));
        Assert.Empty(ItunesMatcher.Parse("""{"results":"nope"}"""));
    }
}

public class ArtworkServiceTests
{
    private static ArtworkService Make(FakeHttp http, string? cache = null, Action<ArtworkConfig>? tweak = null)
    {
        var cfg = new ArtworkConfig { MaxRetries = 2, RequestTimeoutSeconds = 5 };
        tweak?.Invoke(cfg);
        return new ArtworkService(cfg, new HttpClient(http), cache, retryBaseDelay: TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public async Task Resolves_cover_artist_photo_and_links()
    {
        using var svc = Make(FakeHttp.Standard());

        var art = await svc.GetAsync(T.Track());

        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/Music/ram/600x600bb.jpg", art.AlbumArtUrl);
        Assert.Equal("https://cdn-images.dzcdn.net/images/artist/abc123/1000x1000-000000-80-0-0.jpg", art.ArtistImageUrl);
        Assert.Equal("https://music.apple.com/us/album/ram?i=2", art.TrackUrl);
        Assert.Equal("https://music.apple.com/us/album/ram", art.AlbumUrl);
        Assert.Equal("https://music.apple.com/us/artist/daft-punk/5468295", art.ArtistUrl);
    }

    [Fact]
    public async Task Queries_the_right_endpoints()
    {
        var http = FakeHttp.Standard();
        using var svc = Make(http, tweak: c => c.ItunesCountry = "IT");

        await svc.GetAsync(T.Track());

        Assert.Contains(http.Requests, r => r.StartsWith("https://itunes.apple.com/search?term=Get%20Lucky%20Daft%20Punk") &&
                                            r.Contains("entity=song") && r.Contains("country=IT"));
        Assert.Contains(http.Requests, r => r.StartsWith("https://api.deezer.com/search/artist?q=Daft%20Punk"));
    }

    [Fact]
    public async Task Second_lookup_is_served_from_the_memory_cache()
    {
        var http = FakeHttp.Standard();
        using var svc = Make(http);

        await svc.GetAsync(T.Track());
        var before = http.Requests.Count;
        var again = await svc.GetAsync(T.Track());

        Assert.Equal(before, http.Requests.Count);
        Assert.NotNull(again.AlbumArtUrl);
    }

    [Fact]
    public async Task Cache_survives_a_restart_via_the_disk_file()
    {
        var cache = Path.Combine(T.TempDir(), "cache.json");
        using (var first = Make(FakeHttp.Standard(), cache)) await first.GetAsync(T.Track());
        Assert.True(File.Exists(cache));

        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var second = Make(http, cache);
        var art = await second.GetAsync(T.Track());

        Assert.Empty(http.Requests);
        Assert.NotNull(art.AlbumArtUrl);
        Assert.NotNull(art.ArtistImageUrl);
    }

    [Fact]
    public async Task Retries_transient_errors_then_succeeds()
    {
        var calls = 0;
        var http = new FakeHttp(req =>
        {
            if (req.RequestUri!.Host.Contains("deezer")) return FakeHttp.Json(T.DeezerDaftPunk);
            return Interlocked.Increment(ref calls) < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : FakeHttp.Json(T.ItunesGetLucky);
        });
        using var svc = Make(http);

        var art = await svc.GetAsync(T.Track());

        Assert.Equal(3, calls);
        Assert.NotNull(art.AlbumArtUrl);
    }

    [Fact]
    public async Task Network_failure_gives_empty_art_and_is_not_cached()
    {
        var up = false;
        var http = new FakeHttp(req => up
            ? FakeHttp.Standard().Handle(req)
            : throw new HttpRequestException("offline"));
        using var svc = Make(http);

        var down = await svc.GetAsync(T.Track());
        Assert.Equal(ArtworkInfo.Empty, down);

        up = true;   // next track change, the network is back: it must look again instead of remembering the failure
        var back = await svc.GetAsync(T.Track());
        Assert.NotNull(back.AlbumArtUrl);
    }

    [Fact]
    public async Task Unknown_track_is_remembered_as_not_found()
    {
        var http = new FakeHttp(req => req.RequestUri!.Host.Contains("itunes")
            ? FakeHttp.Json("""{"resultCount":0,"results":[]}""")
            : FakeHttp.Json("""{"data":[],"total":0}"""));
        using var svc = Make(http);

        var track = T.Track(title: "Nope", artist: "Nobody", album: "");
        await svc.GetAsync(track);
        var count = http.Requests.Count;
        var art = await svc.GetAsync(track);

        Assert.Equal(count, http.Requests.Count);
        Assert.Null(art.AlbumArtUrl);
    }

    [Fact]
    public async Task Album_search_is_the_fallback_when_the_song_is_not_found()
    {
        var http = new FakeHttp(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("entity=album"))
                return FakeHttp.Json("""{"results":[{"artistName":"Daft Punk","collectionName":"Random Access Memories","artworkUrl100":"https://x/alb/100x100bb.jpg","collectionViewUrl":"https://music.apple.com/alb"}]}""");
            if (uri.Contains("itunes")) return FakeHttp.Json("""{"results":[]}""");
            return FakeHttp.Json(T.DeezerDaftPunk);
        });
        using var svc = Make(http);

        var art = await svc.GetAsync(T.Track(title: "Obscure B-side"));

        Assert.Equal("https://x/alb/600x600bb.jpg", art.AlbumArtUrl);
        Assert.Equal("https://music.apple.com/alb", art.AlbumUrl);
    }

    [Fact]
    public async Task Deezer_placeholder_photo_and_non_exact_names_are_ignored()
    {
        var http = new FakeHttp(req => req.RequestUri!.Host.Contains("itunes")
            ? FakeHttp.Json(T.ItunesGetLucky)
            : FakeHttp.Json("""
              {"data":[
                {"name":"Daft Punk Tribute","link":"https://deezer/1","picture_xl":"https://cdn/artist/zzz/1000x1000.jpg"},
                {"name":"Daft Punk","link":"https://deezer/27","picture_xl":"https://cdn-images.dzcdn.net/images/artist//1000x1000-000000-80-0-0.jpg"}]}
              """));
        using var svc = Make(http);

        var art = await svc.GetAsync(T.Track());

        Assert.Null(art.ArtistImageUrl);                    // caller falls back to the album cover
        Assert.NotNull(art.AlbumArtUrl);
    }

    [Fact]
    public async Task Disabled_or_artist_photo_off_skips_the_requests()
    {
        var http = FakeHttp.Standard();
        using (var off = Make(http, tweak: c => c.Enabled = false))
            Assert.Equal(ArtworkInfo.Empty, await off.GetAsync(T.Track()));
        Assert.Empty(http.Requests);

        using var noPhoto = Make(http, tweak: c => c.UseArtistPhoto = false);
        var art = await noPhoto.GetAsync(T.Track());
        Assert.Equal(0, http.Count("deezer"));
        Assert.NotNull(art.AlbumArtUrl);
        Assert.Null(art.ArtistImageUrl);
    }

    [Fact]
    public async Task Concurrent_lookups_for_the_same_track_share_one_request_burst()
    {
        var http = FakeHttp.Standard();
        using var svc = Make(http);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => svc.GetAsync(T.Track())));

        Assert.Equal(1, http.Count("itunes"));
        Assert.Equal(1, http.Count("deezer"));
    }

    [Fact]
    public async Task Corrupt_disk_cache_is_ignored()
    {
        var cache = Path.Combine(T.TempDir(), "cache.json");
        await File.WriteAllTextAsync(cache, "{{{ garbage");
        using var svc = Make(FakeHttp.Standard(), cache);
        Assert.NotNull((await svc.GetAsync(T.Track())).AlbumArtUrl);
    }
}

internal static class FakeHttpExtensions
{
    public static HttpResponseMessage Handle(this FakeHttp http, HttpRequestMessage req) =>
        req.RequestUri!.Host.Contains("itunes") ? FakeHttp.Json(T.ItunesGetLucky) : FakeHttp.Json(T.DeezerDaftPunk);
}
