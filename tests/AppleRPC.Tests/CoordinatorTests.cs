// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using AppleRPC.Artwork;
using AppleRPC.Media;
using AppleRPC.Presence;

namespace AppleRPC.Tests;

/// <summary>End to end: fake media source -> real artwork service (fake HTTP) -> real presence service -> fake Discord pipe.</summary>
public class CoordinatorTests : IAsyncLifetime
{
    private FakeDiscord _discord = null!;
    private FakeMedia _media = null!;
    private FakeHttp _http = null!;
    private ArtworkService _artwork = null!;
    private PresenceService _presence = null!;
    private RpcCoordinator _coordinator = null!;
    private AppleRPC.Configuration.Config _config = null!;

    public async Task InitializeAsync() => await Build(c => c.PauseClearAfterSeconds = 1);

    private async Task Build(Action<AppleRPC.Configuration.Config> tweak, FakeHttp? http = null)
    {
        _discord = new FakeDiscord();
        _discord.Start();
        _media = new FakeMedia();
        _http = http ?? FakeHttp.Standard();
        _config = T.Config(tweak);
        _artwork = new ArtworkService(_config.Artwork, new HttpClient(_http), retryBaseDelay: TimeSpan.FromMilliseconds(5));
        _presence = new PresenceService(_config, () => new DiscordIpcClient(T.AppId, _discord.PipeFormat),
            retryDelay: TimeSpan.FromMilliseconds(100), minInterval: TimeSpan.FromMilliseconds(200));
        _coordinator = new RpcCoordinator(_config, _media, _artwork, _presence);
        await _coordinator.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _coordinator.Dispose();
        await _presence.DisposeAsync();
        _artwork.Dispose();
        await _discord.DisposeAsync();
    }

    [Fact]
    public async Task Playing_track_reaches_discord_with_art_links_timer_and_button()
    {
        _media.Emit(T.Track(posSec: 30, durSec: 369));

        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 1));
        var a = _discord.Activities.First();
        Assert.Equal("Get Lucky", a.GetProperty("details").GetString());
        Assert.Equal("Daft Punk • Random Access Memories", a.GetProperty("state").GetString());
        Assert.Equal("https://music.apple.com/us/album/ram?i=2", a.GetProperty("details_url").GetString());
        var assets = a.GetProperty("assets");
        Assert.EndsWith("600x600bb.jpg", assets.GetProperty("large_image").GetString());
        Assert.Contains("dzcdn.net", assets.GetProperty("small_image").GetString());
        var ts = a.GetProperty("timestamps");
        Assert.Equal(369, ts.GetProperty("end").GetInt64() - ts.GetProperty("start").GetInt64());
        Assert.Equal("⭐ Star on GitHub", a.GetProperty("buttons")[0].GetProperty("label").GetString());
        Assert.Equal(StatusKind.Playing, _coordinator.Status.Kind);
    }

    [Fact]
    public async Task Pause_drops_the_timer_then_clears_after_the_configured_delay()
    {
        var t0 = DateTimeOffset.UtcNow;
        _media.Emit(T.Track(at: t0));
        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 1));

        _media.Emit(T.Track(status: PlaybackStatus.Paused, at: t0));
        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 2));
        Assert.False(_discord.Activities.Last().TryGetProperty("timestamps", out _));
        Assert.Equal(StatusKind.Paused, _coordinator.Status.Kind);

        // PauseClearAfterSeconds = 1
        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 3, 6000));
        Assert.Equal(JsonValueKind.Undefined, _discord.Activities.Last().ValueKind);
    }

    [Fact]
    public async Task Repeated_paused_events_do_not_restart_the_pause_clock_or_resurrect_a_cleared_presence()
    {
        var t0 = DateTimeOffset.UtcNow;
        var paused = T.Track(status: PlaybackStatus.Paused, at: t0);
        _media.Emit(paused);
        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 1));
        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 2, 6000));    // cleared after 1 s

        _media.Emit(paused);                                                            // e.g. the watcher's safety refresh
        await Task.Delay(700);

        Assert.Equal(2, _discord.Activities.Count);
        Assert.Equal(JsonValueKind.Undefined, _discord.Activities.Last().ValueKind);
    }

    [Fact]
    public async Task Resuming_shows_the_presence_again()
    {
        var t0 = DateTimeOffset.UtcNow;
        _media.Emit(T.Track(status: PlaybackStatus.Paused, at: t0));
        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 2, 6000));    // shown, then cleared

        _media.Emit(T.Track(at: DateTimeOffset.UtcNow));

        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 3, 6000));
        Assert.True(_discord.Activities.Last().TryGetProperty("timestamps", out _));
    }

    [Fact]
    public async Task Losing_the_apple_music_session_clears_the_presence()
    {
        _media.Emit(T.Track());
        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 1));

        _media.Emit(null);

        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 2));
        Assert.Equal(JsonValueKind.Undefined, _discord.Activities.Last().ValueKind);
        Assert.Equal(StatusKind.WaitingForAppleMusic, _coordinator.Status.Kind);
    }

    [Fact]
    public async Task Offline_artwork_still_shows_the_presence_with_the_default_asset()
    {
        await DisposeAsync();
        await Build(c => c.PauseClearAfterSeconds = -1, new FakeHttp(_ => throw new HttpRequestException("offline")));

        _media.Emit(T.Track());

        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 1, 10000));
        var assets = _discord.Activities.First().GetProperty("assets");
        Assert.Equal("applerpc", assets.GetProperty("large_image").GetString());
        Assert.False(assets.TryGetProperty("small_image", out _));
    }

    [Fact]
    public async Task Without_an_application_id_nothing_is_sent_and_status_says_so()
    {
        await DisposeAsync();
        await Build(c => c.DiscordApplicationId = "");

        _media.Emit(T.Track());
        await Task.Delay(400);

        Assert.Equal(StatusKind.NotConfigured, _coordinator.Status.Kind);
        Assert.Equal(0, _discord.HandshakeCount);
    }

    [Fact]
    public async Task Fast_skipping_sends_only_the_final_song()
    {
        _media.Emit(T.Track(title: "One"));
        Assert.True(await T.WaitUntil(() => _discord.Activities.Count == 1));

        _media.Emit(T.Track(title: "Two"));
        _media.Emit(T.Track(title: "Three"));
        _media.Emit(T.Track(title: "Four"));

        Assert.True(await T.WaitUntil(() => _discord.Activities.Count >= 2));
        await Task.Delay(600);
        Assert.Equal(new[] { "One", "Four" }, _discord.Activities.Select(x => x.GetProperty("details").GetString()).ToArray());
    }
}
