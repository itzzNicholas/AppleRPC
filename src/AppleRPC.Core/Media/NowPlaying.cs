// SPDX-License-Identifier: AGPL-3.0-only
namespace AppleRPC.Media;

public enum PlaybackStatus { Stopped, Paused, Playing }

/// <summary>A platform-neutral snapshot of what the player is doing right now.</summary>
/// <param name="Position">Playback position <b>at <see cref="SampledAt"/></b>.</param>
/// <param name="Duration">Zero when unknown (radio, streams).</param>
public sealed record NowPlaying(
    string Title,
    string Artist,
    string Album,
    PlaybackStatus Status,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset SampledAt)
{
    public bool HasDuration => Duration > TimeSpan.Zero;

    /// <summary>Position extrapolated to <paramref name="now"/> (only moves while playing).</summary>
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        var pos = Position;
        if (Status == PlaybackStatus.Playing && now > SampledAt) pos += now - SampledAt;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (HasDuration && pos > Duration) pos = Duration;
        return pos;
    }
}

/// <summary>Anything that can tell us what's playing. Implemented by the Windows MediaWatcher.</summary>
public interface IMediaSource
{
    /// <summary>Raised on every relevant change. <c>null</c> = no Apple Music session available.</summary>
    event Action<NowPlaying?>? Changed;

    Task StartAsync();
}
