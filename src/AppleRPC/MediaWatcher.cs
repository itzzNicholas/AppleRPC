// SPDX-License-Identifier: AGPL-3.0-only
using AppleRPC.Configuration;
using AppleRPC.Logging;
using AppleRPC.Media;
using Windows.Media.Control;

namespace AppleRPC;

/// <summary>
/// Watches the Apple Music session of the Windows media transport controls (the same source that feeds
/// the volume flyout / lock screen) and raises <see cref="Changed"/> when the song, play state or position jumps.
/// It is event-driven: SMTC events -> a short debounce -> one read. A slow safety timer only exists to
/// recover from a missed event and raises nothing unless something actually changed.
/// </summary>
public sealed class MediaWatcher : IMediaSource, IDisposable
{
    private static readonly TimeSpan EventDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SafetyRefresh = TimeSpan.FromSeconds(30);

    /// <summary>A change of the implied start time (now - position) bigger than this is a seek, not clock jitter.</summary>
    private static readonly TimeSpan SeekThreshold = TimeSpan.FromSeconds(2);

    private readonly Config _config;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _raiseGate = new();
    private readonly System.Threading.Timer _debounce;
    private readonly System.Threading.Timer _safety;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private NowPlaying? _lastRaised;
    private bool _hasRaised;
    private volatile bool _disposed;

    public event Action<NowPlaying?>? Changed;

    public MediaWatcher(Config config)
    {
        _config = config;
        _debounce = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _safety = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += OnSessionsChanged;
        _manager.CurrentSessionChanged += OnSessionsChanged;

        await RefreshAsync();
        _safety.Change(SafetyRefresh, SafetyRefresh);
    }

    // ---------------------------------------------------------------- events

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args) => ScheduleRefresh();
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => ScheduleRefresh();
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => ScheduleRefresh();
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => ScheduleRefresh();

    /// <summary>SMTC fires several events per song change; wait a moment and read once.</summary>
    private void ScheduleRefresh()
    {
        if (_disposed) return;
        try { _debounce.Change(EventDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    // ---------------------------------------------------------------- reading

    private async Task RefreshAsync()
    {
        if (_disposed || _manager is null) return;
        await _refreshLock.WaitAsync();
        try
        {
            if (_disposed) return;
            UpdateSubscribedSession(PickSession());

            NowPlaying? snapshot = null;
            if (_session is not null)
            {
                var read = await ReadAsync(_session);
                if (read.Transient) return;   // mid-transition: wait for the next event
                snapshot = read.Value;
            }
            Raise(snapshot);
        }
        catch (Exception ex)
        {
            // COMException when the app closes mid-read, etc. The next event will retry.
            Log.Warn("Could not read the media session.", ex);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>The Apple Music session, if any (a Playing one wins when there are several).</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        var matches = _manager!.GetSessions()
            .Where(s => _config.AppleMusicSourceIds.Any(id =>
                s.SourceAppUserModelId.Contains(id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return matches.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
               ?? matches.FirstOrDefault();
    }

    private void UpdateSubscribedSession(GlobalSystemMediaTransportControlsSession? next)
    {
        if (ReferenceEquals(next, _session)) return;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = next;
        if (next is not null)
        {
            Log.Info($"Attached to media session '{next.SourceAppUserModelId}'.");
            next.MediaPropertiesChanged += OnMediaPropertiesChanged;
            next.PlaybackInfoChanged += OnPlaybackInfoChanged;
            next.TimelinePropertiesChanged += OnTimelineChanged;
        }
        else
        {
            Log.Info("Apple Music media session went away.");
        }
    }

    private static async Task<(bool Transient, NowPlaying? Value)> ReadAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var info = session.GetPlaybackInfo();
        var status = info.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackStatus.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackStatus.Stopped,
            _ => (PlaybackStatus?)null,   // Opened / Changing: the app is switching track
        };
        if (status is null) return (true, null);

        var props = await session.TryGetMediaPropertiesAsync();
        if (string.IsNullOrWhiteSpace(props.Title))
            return status == PlaybackStatus.Stopped ? (false, null) : (true, null);   // metadata not ready yet

        var (artist, album) = TrackText.SplitArtistAlbum(props.Artist, props.AlbumTitle);

        var timeline = session.GetTimelineProperties();
        var duration = timeline.EndTime - timeline.StartTime;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        var now = DateTimeOffset.UtcNow;
        var position = timeline.Position - timeline.StartTime;
        // The timeline is only refreshed on events; add the time that passed since it was last updated.
        if (status == PlaybackStatus.Playing && timeline.LastUpdatedTime.Year > 2000)
        {
            var elapsed = now - timeline.LastUpdatedTime.ToUniversalTime();
            if (elapsed > TimeSpan.Zero) position += elapsed * (info.PlaybackRate ?? 1.0);
        }
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration) position = duration;

        return (false, new NowPlaying(props.Title.Trim(), artist, album, status.Value, position, duration, now));
    }

    // ---------------------------------------------------------------- raising

    private void Raise(NowPlaying? snapshot)
    {
        lock (_raiseGate)
        {
            if (_hasRaised && !IsMaterialChange(_lastRaised, snapshot)) return;
            _lastRaised = snapshot;
            _hasRaised = true;
        }
        Changed?.Invoke(snapshot);
    }

    private static bool IsMaterialChange(NowPlaying? a, NowPlaying? b)
    {
        if (a is null || b is null) return !(a is null && b is null);
        if (a.Title != b.Title || a.Artist != b.Artist || a.Album != b.Album || a.Status != b.Status) return true;
        if (a.Duration != b.Duration) return true;

        // Same song, same state: only a seek (a jump of the implied start time) matters.
        var startA = a.SampledAt - a.PositionAt(a.SampledAt);
        var startB = b.SampledAt - b.PositionAt(b.SampledAt);
        if (a.Status == PlaybackStatus.Playing) return (startA - startB).Duration() > SeekThreshold;
        return (a.Position - b.Position).Duration() > SeekThreshold;
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Dispose();
        _safety.Dispose();
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnSessionsChanged;
        }
        UpdateSubscribedSession(null);
        _refreshLock.Dispose();
    }
}
