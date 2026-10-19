// SPDX-License-Identifier: AGPL-3.0-only
using AppleRPC.Artwork;
using AppleRPC.Configuration;
using AppleRPC.Logging;
using AppleRPC.Media;
using AppleRPC.Presence;

namespace AppleRPC;

public enum StatusKind { NotConfigured, WaitingForAppleMusic, Playing, Paused }

/// <summary>Everything the tray needs to describe what the app is doing.</summary>
public sealed record AppStatus(StatusKind Kind, NowPlaying? Track, PresenceState Discord, string? DiscordUser, string? Error);

/// <summary>
/// Glue: media events in -> (artwork lookup) -> activity -> <see cref="PresenceService"/>.
/// Reacts to events; the only timers are the pause-clear timeout and whatever the media source runs.
/// </summary>
public sealed class RpcCoordinator : IDisposable
{
    private readonly Config _config;
    private readonly IMediaSource _media;
    private readonly ArtworkService _artwork;
    private readonly PresenceService _presence;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private long _version;
    private NowPlaying? _current;
    private DateTimeOffset? _pausedSince;
    private CancellationTokenSource? _pauseTimer;

    public AppStatus Status { get; private set; }
    public event Action<AppStatus>? StatusChanged;

    public RpcCoordinator(Config config, IMediaSource media, ArtworkService artwork, PresenceService presence, TimeProvider? clock = null)
    {
        _config = config;
        _media = media;
        _artwork = artwork;
        _presence = presence;
        _clock = clock ?? TimeProvider.System;
        Status = BuildStatus();
    }

    public async Task StartAsync()
    {
        if (!_config.HasValidApplicationId)
        {
            Log.Warn("DiscordApplicationId is missing or invalid; presence is disabled until it is set in appsettings.json.");
            Publish();
            return;
        }

        _presence.StateChanged += Publish;
        _media.Changed += OnMediaChanged;
        _presence.Start();
        await _media.StartAsync().ConfigureAwait(false);
        Publish();
    }

    // ---------------------------------------------------------------- media events

    private void OnMediaChanged(NowPlaying? now)
    {
        long version;
        lock (_gate)
        {
            version = ++_version;
            // Remember when this pause began; repeated events for the same paused track must not restart the clock.
            var stillPaused = now is { Status: PlaybackStatus.Paused } && _current is { Status: PlaybackStatus.Paused } prev
                              && prev.Title == now.Title && prev.Artist == now.Artist && prev.Album == now.Album;
            _pausedSince = now is { Status: PlaybackStatus.Paused } ? (stillPaused ? _pausedSince : _clock.GetUtcNow()) : null;
            _current = now;
            CancelPauseTimerLocked();
        }
        Publish();
        _ = ProcessAsync(version, now);
    }

    private async Task ProcessAsync(long version, NowPlaying? now)
    {
        try
        {
            if (now is null || now.Status == PlaybackStatus.Stopped)
            {
                _presence.SetActivity(null);
                return;
            }

            if (now.Status == PlaybackStatus.Paused && PauseTimeoutLeft() is { } left && left <= TimeSpan.Zero)
            {
                _presence.SetActivity(null);   // paused for longer than allowed (0 = clear immediately)
                return;
            }

            var art = await GetArtworkWithinBudgetAsync(now, version).ConfigureAwait(false);
            if (!IsCurrent(version)) return;   // something newer arrived while we were looking up the cover

            _presence.SetActivity(ActivityBuilder.Build(now, art, _config, _clock.GetUtcNow()));

            if (now.Status == PlaybackStatus.Paused && PauseTimeoutLeft() is { } remaining)
                StartPauseTimer(version, remaining);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to process a media update.", ex);
        }
    }

    /// <summary>
    /// Normally the lookup is instant (cache) or quick. If it's slow we don't hold the presence back:
    /// we publish without art right away and publish again once the art arrives (the debouncer coalesces).
    /// </summary>
    private async Task<ArtworkInfo> GetArtworkWithinBudgetAsync(NowPlaying now, long version)
    {
        var lookup = _artwork.GetAsync(now);
        var budget = TimeSpan.FromSeconds(_config.Artwork.LookupBudgetSeconds);

        var finished = await Task.WhenAny(lookup, Task.Delay(budget, _clock)).ConfigureAwait(false);
        if (finished == lookup) return await lookup.ConfigureAwait(false);

        Log.Warn($"Artwork lookup is slow (> {budget.TotalSeconds:0}s); showing the presence without it for now.");
        _ = lookup.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && IsCurrent(version) && _current is { } cur)
                _presence.SetActivity(ActivityBuilder.Build(cur, t.Result, _config, _clock.GetUtcNow()));
        }, TaskScheduler.Default);
        return ArtworkInfo.Empty;
    }

    private bool IsCurrent(long version)
    {
        lock (_gate) return version == _version;
    }

    // ---------------------------------------------------------------- pause timeout

    /// <summary>Time left before a pause clears the presence; null = never (PauseClearAfterSeconds = -1).</summary>
    private TimeSpan? PauseTimeoutLeft()
    {
        if (_config.PauseClearAfterSeconds < 0) return null;
        DateTimeOffset since;
        lock (_gate) since = _pausedSince ?? _clock.GetUtcNow();
        return TimeSpan.FromSeconds(_config.PauseClearAfterSeconds) - (_clock.GetUtcNow() - since);
    }

    private void StartPauseTimer(long version, TimeSpan delay)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (version != _version) { cts.Dispose(); return; }
            CancelPauseTimerLocked();
            _pauseTimer = cts;
        }

        _ = Task.Delay(delay, _clock, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || !IsCurrent(version)) return;
            Log.Info($"Paused for over {_config.PauseClearAfterSeconds}s: clearing the presence.");
            _presence.SetActivity(null);
        }, TaskScheduler.Default);
    }

    private void CancelPauseTimerLocked()
    {
        _pauseTimer?.Cancel();
        _pauseTimer?.Dispose();
        _pauseTimer = null;
    }

    // ---------------------------------------------------------------- status

    private void Publish()
    {
        var status = BuildStatus();
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private AppStatus BuildStatus()
    {
        NowPlaying? track;
        lock (_gate) track = _current;

        var kind = !_config.HasValidApplicationId ? StatusKind.NotConfigured
            : track is null || track.Status == PlaybackStatus.Stopped ? StatusKind.WaitingForAppleMusic
            : track.Status == PlaybackStatus.Playing ? StatusKind.Playing
            : StatusKind.Paused;

        return new AppStatus(kind, kind is StatusKind.Playing or StatusKind.Paused ? track : null,
            _presence.State, _presence.DiscordUser, _presence.LastError);
    }

    public void Dispose()
    {
        _media.Changed -= OnMediaChanged;
        _presence.StateChanged -= Publish;
        lock (_gate)
        {
            _version++;
            CancelPauseTimerLocked();
        }
    }
}
