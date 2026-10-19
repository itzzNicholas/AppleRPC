// SPDX-License-Identifier: AGPL-3.0-only
namespace AppleRPC.Artwork;

/// <summary>Everything we learned about a track from iTunes / Deezer. All members are optional.</summary>
public sealed record ArtworkInfo(
    string? AlbumArtUrl = null,
    string? ArtistImageUrl = null,
    string? TrackUrl = null,
    string? AlbumUrl = null,
    string? ArtistUrl = null)
{
    public static readonly ArtworkInfo Empty = new();
}
