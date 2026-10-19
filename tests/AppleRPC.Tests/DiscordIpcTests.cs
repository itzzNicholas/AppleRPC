// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using AppleRPC.Media;
using AppleRPC.Presence;

namespace AppleRPC.Tests;

public class DiscordIpcClientTests
{
    private static ActivityPayload Sample(string title = "Get Lucky") =>
        ActivityBuilder.Build(T.Track(title: title), null, T.Config(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Handshake_sends_version_and_client_id_and_reads_the_user()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var client = new DiscordIpcClient(T.AppId, discord.PipeFormat);

        await client.ConnectAsync();

        Assert.True(client.IsConnected);
        Assert.Equal("Test User", client.UserName);
        using var hs = JsonDocument.Parse(discord.HandshakeJson);
        Assert.Equal(1, hs.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(T.AppId, hs.RootElement.GetProperty("client_id").GetString());
    }

    [Fact]
    public async Task Set_activity_sends_the_expected_json()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var client = new DiscordIpcClient(T.AppId, discord.PipeFormat);
        await client.ConnectAsync();

        await client.SetActivityAsync(Sample());

        Assert.True(discord.Activities.TryDequeue(out var activity));
        Assert.Equal(2, activity.GetProperty("type").GetInt32());
        Assert.Equal(2, activity.GetProperty("status_display_type").GetInt32());
        Assert.Equal("Get Lucky", activity.GetProperty("details").GetString());
        Assert.Equal("⭐ Star on GitHub", activity.GetProperty("buttons")[0].GetProperty("label").GetString());   // emoji not mangled into \u escapes badly
        Assert.True(activity.GetProperty("timestamps").TryGetProperty("end", out _));

        using var req = JsonDocument.Parse(discord.RawRequests.Single());
        Assert.Equal("SET_ACTIVITY", req.RootElement.GetProperty("cmd").GetString());
        Assert.Equal(Environment.ProcessId, req.RootElement.GetProperty("args").GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task Null_activity_clears_the_presence()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var client = new DiscordIpcClient(T.AppId, discord.PipeFormat);
        await client.ConnectAsync();

        await client.SetActivityAsync(null);

        Assert.True(discord.Activities.TryDequeue(out var activity));
        Assert.Equal(JsonValueKind.Undefined, activity.ValueKind);   // no "activity" key at all
    }

    [Fact]
    public async Task Discord_error_responses_surface_as_exceptions()
    {
        await using var discord = new FakeDiscord { RejectActivitiesWith = "Invalid payload" };
        discord.Start();
        await using var client = new DiscordIpcClient(T.AppId, discord.PipeFormat);
        await client.ConnectAsync();

        var ex = await Assert.ThrowsAsync<DiscordRpcException>(() => client.SetActivityAsync(Sample()));

        Assert.Equal(4000, ex.Code);
        Assert.Contains("Invalid payload", ex.Message);
        Assert.True(client.IsConnected);   // a rejected payload is not a lost connection
    }

    [Fact]
    public async Task Invalid_client_id_close_frame_fails_the_connect()
    {
        await using var discord = new FakeDiscord { CloseHandshakeWith = """{"code":4000,"message":"Invalid Client ID"}""" };
        discord.Start();
        await using var client = new DiscordIpcClient("1", discord.PipeFormat);

        var ex = await Assert.ThrowsAsync<DiscordRpcException>(() => client.ConnectAsync());

        Assert.Contains("Invalid Client ID", ex.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Answers_pings_with_pongs()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var client = new DiscordIpcClient(T.AppId, discord.PipeFormat);
        await client.ConnectAsync();

        await discord.PingAsync("""{"nonce":"xyz"}""");

        Assert.True(await T.WaitUntil(() => !discord.Pongs.IsEmpty));
        Assert.Contains("xyz", discord.Pongs.First());
    }

    [Fact]
    public async Task Reports_disconnects_when_discord_goes_away()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var client = new DiscordIpcClient(T.AppId, discord.PipeFormat);
        var raised = new TaskCompletionSource();
        client.Disconnected += () => raised.TrySetResult();
        await client.ConnectAsync();

        discord.DropConnection();

        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => client.SetActivityAsync(Sample()));
    }

    [Fact]
    public async Task Connect_fails_cleanly_when_discord_is_not_running()
    {
        await using var client = new DiscordIpcClient(T.AppId, "arpc-nobody-home-" + Guid.NewGuid().ToString("N")[..6] + "{0}");
        await Assert.ThrowsAsync<IOException>(() => client.ConnectAsync());
    }
}

public class PresenceServiceTests
{
    private static PresenceService Make(FakeDiscord discord, double minIntervalMs = 400, double retryMs = 150) =>
        new(T.Config(), () => new DiscordIpcClient(T.AppId, discord.PipeFormat),
            retryDelay: TimeSpan.FromMilliseconds(retryMs), minInterval: TimeSpan.FromMilliseconds(minIntervalMs));

    private static ActivityPayload Act(string title, int pos = 10) =>
        ActivityBuilder.Build(T.Track(title: title, posSec: pos), null, T.Config(), DateTimeOffset.UtcNow);

    private static string Details(JsonElement e) => e.GetProperty("details").GetString()!;

    [Fact]
    public async Task First_update_goes_out_immediately_and_connects_lazily()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var svc = Make(discord);
        svc.Start();

        svc.SetActivity(Act("One"));

        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 1));
        Assert.Equal("One", Details(discord.Activities.First()));
        Assert.Equal(PresenceState.Connected, svc.State);
        Assert.Equal("Test User", svc.DiscordUser);
    }

    [Fact]
    public async Task Rapid_changes_are_debounced_and_only_the_latest_is_sent()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var svc = Make(discord, minIntervalMs: 500);
        svc.Start();

        svc.SetActivity(Act("One"));
        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 1));

        svc.SetActivity(Act("Two"));      // inside the interval: must wait
        svc.SetActivity(Act("Three"));    // supersedes Two before it was ever sent
        await Task.Delay(150);
        Assert.Single(discord.Activities);

        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 2));
        await Task.Delay(700);            // nothing else may sneak out (e.g. the superseded "Two")
        Assert.Equal(new[] { "One", "Three" }, discord.Activities.Select(Details).ToArray());
    }

    [Fact]
    public async Task Unchanged_activity_is_not_sent_again()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var svc = Make(discord, minIntervalMs: 150);
        svc.Start();

        svc.SetActivity(Act("One"));
        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 1));

        svc.SetActivity(Act("One"));      // same song, same position (only timestamp jitter)
        svc.SetActivity(Act("One"));
        await Task.Delay(500);

        Assert.Single(discord.Activities);
    }

    [Fact]
    public async Task A_seek_is_sent_as_an_update()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var svc = Make(discord, minIntervalMs: 150);
        svc.Start();

        svc.SetActivity(Act("One", pos: 10));
        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 1));
        svc.SetActivity(Act("One", pos: 150));

        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 2));
    }

    [Fact]
    public async Task Clear_is_sent_after_an_activity_but_not_when_nothing_is_shown()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var svc = Make(discord, minIntervalMs: 150);
        svc.Start();

        svc.SetActivity(null);            // nothing displayed yet: no need to even connect
        await Task.Delay(300);
        Assert.Equal(0, discord.HandshakeCount);

        svc.SetActivity(Act("One"));
        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 1));
        svc.SetActivity(null);

        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 2));
        Assert.Equal(JsonValueKind.Undefined, discord.Activities.Last().ValueKind);
    }

    [Fact]
    public async Task Waits_for_discord_to_start_and_then_shows_the_pending_activity()
    {
        await using var discord = new FakeDiscord();       // pipe not listening yet
        await using var svc = Make(discord, retryMs: 100);
        svc.Start();

        svc.SetActivity(Act("Early bird"));
        await Task.Delay(400);
        Assert.Equal(PresenceState.Disconnected, svc.State);

        discord.Start();                                    // "Discord launches"

        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 1, 8000));
        Assert.Equal("Early bird", Details(discord.Activities.First()));
    }

    [Fact]
    public async Task Reconnects_and_resends_when_discord_restarts()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        await using var svc = Make(discord, minIntervalMs: 150, retryMs: 100);
        svc.Start();

        svc.SetActivity(Act("One"));
        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 1));

        discord.DropConnection();                           // Discord crashed / restarted

        Assert.True(await T.WaitUntil(() => discord.HandshakeCount == 2, 8000));
        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 2, 8000));
        Assert.Equal("One", Details(discord.Activities.Last()));
    }

    [Fact]
    public async Task A_rejected_payload_is_not_retried_in_a_loop()
    {
        await using var discord = new FakeDiscord { RejectActivitiesWith = "nope" };
        discord.Start();
        await using var svc = Make(discord, minIntervalMs: 150);
        svc.Start();

        svc.SetActivity(Act("One"));
        Assert.True(await T.WaitUntil(() => svc.LastError is not null));
        await Task.Delay(600);

        Assert.Single(discord.Activities);
        Assert.Contains("nope", svc.LastError);
    }

    [Fact]
    public async Task Dispose_clears_the_presence_on_the_way_out()
    {
        await using var discord = new FakeDiscord();
        discord.Start();
        var svc = Make(discord);
        svc.Start();
        svc.SetActivity(Act("One"));
        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 1));

        await svc.DisposeAsync();

        Assert.True(await T.WaitUntil(() => discord.Activities.Count == 2));
        Assert.Equal(JsonValueKind.Undefined, discord.Activities.Last().ValueKind);
    }

    [Fact]
    public async Task Invalid_application_id_is_reported_and_not_hammered()
    {
        await using var discord = new FakeDiscord { CloseHandshakeWith = """{"code":4000,"message":"Invalid Client ID"}""" };
        discord.Start();
        await using var svc = Make(discord, retryMs: 5000);   // "fatal" retry delay is long
        svc.Start();

        svc.SetActivity(Act("One"));

        Assert.True(await T.WaitUntil(() => svc.LastError is not null));
        Assert.Contains("Invalid Client ID", svc.LastError);
        await Task.Delay(400);
        Assert.Equal(1, discord.HandshakeCount);
    }
}
