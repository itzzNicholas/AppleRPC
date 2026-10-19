// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json.Serialization;

namespace AppleRPC.Presence;

/// <summary>
/// The <c>activity</c> object sent with SET_ACTIVITY (https://discord.com/developers/docs/events/gateway-events#activity-object).
/// Null members are omitted from the JSON.
/// </summary>
public sealed record ActivityPayload
{
    /// <summary>2 = Listening ("Listening to Apple Music").</summary>
    [JsonPropertyName("type")] public int Type { get; init; } = 2;

    /// <summary>0 = app name, 1 = state, 2 = details: which field the member-list status shows.</summary>
    [JsonPropertyName("status_display_type")] public int? StatusDisplayType { get; init; }

    [JsonPropertyName("details")] public string? Details { get; init; }
    [JsonPropertyName("details_url")] public string? DetailsUrl { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("state_url")] public string? StateUrl { get; init; }
    [JsonPropertyName("timestamps")] public ActivityTimestamps? Timestamps { get; init; }
    [JsonPropertyName("assets")] public ActivityAssets? Assets { get; init; }
    [JsonPropertyName("buttons")] public IReadOnlyList<ActivityButton>? Buttons { get; init; }

    /// <summary>
    /// True when <paramref name="other"/> would look the same on Discord. Timestamps are compared with a tolerance
    /// so the sub-second jitter of recomputing "now - position" doesn't trigger a pointless update; a real
    /// seek or track change moves them by far more than that.
    /// </summary>
    public bool IsEquivalentTo(ActivityPayload? other, TimeSpan? timestampTolerance = null)
    {
        if (other is null) return false;
        var tol = (long)(timestampTolerance ?? TimeSpan.FromSeconds(2)).TotalSeconds;

        return Type == other.Type
            && StatusDisplayType == other.StatusDisplayType
            && Details == other.Details && DetailsUrl == other.DetailsUrl
            && State == other.State && StateUrl == other.StateUrl
            && Equals(Assets, other.Assets)
            && ButtonsEqual(Buttons, other.Buttons)
            && TimestampsClose(Timestamps, other.Timestamps, tol);
    }

    private static bool ButtonsEqual(IReadOnlyList<ActivityButton>? a, IReadOnlyList<ActivityButton>? b)
    {
        var x = a ?? Array.Empty<ActivityButton>();
        var y = b ?? Array.Empty<ActivityButton>();
        return x.SequenceEqual(y);
    }

    private static bool TimestampsClose(ActivityTimestamps? a, ActivityTimestamps? b, long tol)
    {
        if (a is null || b is null) return a is null && b is null;
        return Close(a.Start, b.Start, tol) && Close(a.End, b.End, tol);

        static bool Close(long? x, long? y, long t) =>
            x is null || y is null ? x is null && y is null : Math.Abs(x.Value - y.Value) <= t;
    }
}

/// <summary>Unix time in <b>seconds</b> (as in Discord's own SET_ACTIVITY example).</summary>
public sealed record ActivityTimestamps(
    [property: JsonPropertyName("start")] long? Start,
    [property: JsonPropertyName("end")] long? End);

public sealed record ActivityAssets
{
    [JsonPropertyName("large_image")] public string? LargeImage { get; init; }
    [JsonPropertyName("large_text")] public string? LargeText { get; init; }
    [JsonPropertyName("large_url")] public string? LargeUrl { get; init; }
    [JsonPropertyName("small_image")] public string? SmallImage { get; init; }
    [JsonPropertyName("small_text")] public string? SmallText { get; init; }
    [JsonPropertyName("small_url")] public string? SmallUrl { get; init; }
}

public sealed record ActivityButton(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("url")] string Url);
