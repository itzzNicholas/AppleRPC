// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AppleRPC.Configuration;
using AppleRPC.Logging;
using AppleRPC.Media;

namespace AppleRPC.Artwork;

/// <summary>
/// Finds the album cover (iTunes Search API) and the artist photo (Deezer public API) for a track.
/// Results are cached in memory and on disk; network calls have a timeout and a small exponential-backoff retry.
/// <see cref="GetAsync"/> never throws (apart from cancellation): on any failure you get <see cref="ArtworkInfo.Empty"/>-like data.
/// </summary>
public sealed class ArtworkService : IDisposable
{
    public const string UserAgent = "AppleRPC/1.0 (+https://github.com/itzzNicholas/AppleRPC)";
    private const int MaxCacheEntries = 1500;
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromHours(24);

    private readonly ArtworkConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string? _cachePath;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retryBaseDelay;
    private readonly object _cacheLock = new();
    private readonly ConcurrentDictionary<string, Task<ArtworkInfo>> _inflight = new();
    private CacheFile _cache = new();

    /// <param name="http">Inject a client (tests) or leave null to create one with the configured timeout.</param>
    /// <param name="cachePath">JSON file for the persistent cache; null = memory only.</param>
    public ArtworkService(ArtworkConfig config, HttpClient? http = null, string? cachePath = null, TimeProvider? clock = null, TimeSpan? retryBaseDelay = null)
    {
        _config = config;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(400);
        _clock = clock ?? TimeProvider.System;
        _cachePath = cachePath;
        if (http is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
        LoadCache();
    }

    public Task<ArtworkInfo> GetAsync(NowPlaying track, CancellationToken ct = default)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(track.Title)) return Task.FromResult(ArtworkInfo.Empty);

        var key = TrackKey(track);

        // Share one lookup between callers asking for the same track at the same time.
        var completion = new TaskCompletionSource<ArtworkInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _inflight.GetOrAdd(key, completion.Task);
        if (!ReferenceEquals(running, completion.Task)) return running.WaitAsync(ct);   // someone else is already on it

        _ = Task.Run(async () =>
        {
            var result = await ResolveSafelyAsync(track, key).ConfigureAwait(false);
            // Unregister *before* completing: whoever asks next starts a fresh lookup instead of reusing this finished one.
            _inflight.TryRemove(key, out _);
            completion.SetResult(result);
        });
        return completion.Task.WaitAsync(ct);
    }

    private async Task<ArtworkInfo> ResolveSafelyAsync(NowPlaying track, string key)
    {
        try { return await ResolveAsync(track, key).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Log.Warn($"Artwork lookup failed for '{track.Title}'", ex);
            return ArtworkInfo.Empty;
        }
    }

    // ---------------------------------------------------------------- resolution

    private async Task<ArtworkInfo> ResolveAsync(NowPlaying track, string key)
    {
        var song = await GetTrackAsync(track, key).ConfigureAwait(false);
        var artist = _config.UseArtistPhoto
            ? await GetArtistAsync(track, song).ConfigureAwait(false)
            : null;

        return new ArtworkInfo(
            AlbumArtUrl: song?.AlbumArtUrl,
            ArtistImageUrl: artist?.ImageUrl,
            TrackUrl: song?.TrackUrl,
            AlbumUrl: song?.AlbumUrl,
            ArtistUrl: song?.ArtistUrl ?? artist?.Url);
    }

    private async Task<TrackEntry?> GetTrackAsync(NowPlaying track, string key)
    {
        if (TryGetCached(_cache.Tracks, key, out var cached)) return cached.Found ? cached : null;

        var (song, failed) = await SearchItunesAsync(track).ConfigureAwait(false);
        if (failed) return null;   // network trouble: don't remember it, try again next time

        var entry = song is null
            ? new TrackEntry { Found = false }
            : new TrackEntry
            {
                Found = true,
                AlbumArtUrl = ItunesMatcher.UpscaleArtwork(song.ArtworkUrl100),
                TrackUrl = song.TrackViewUrl,
                AlbumUrl = song.CollectionViewUrl,
                ArtistUrl = song.ArtistViewUrl,
                ItunesArtistName = song.ArtistName,
            };
        Store(_cache.Tracks, key, entry);
        return entry.Found ? entry : null;
    }

    /// <summary>Track search first; if that finds nothing and we know the album, an album search still gives us a cover.</summary>
    private async Task<(ItunesSong? Song, bool Failed)> SearchItunesAsync(NowPlaying t)
    {
        var country = Uri.EscapeDataString(_config.ItunesCountry);

        var term = Uri.EscapeDataString($"{t.Title} {TrackText.PrimaryArtist(t.Artist)}");
        var (ok, body) = await FetchAsync(
            new Uri($"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=25&country={country}")).ConfigureAwait(false);
        if (!ok) return (null, true);

        var song = ItunesMatcher.PickBest(ItunesMatcher.Parse(body!), t.Title, t.Artist, t.Album);
        if (song is not null || string.IsNullOrWhiteSpace(t.Album)) return (song, false);

        // Fallback: search by album name only (the cover is what we want).
        var albumTerm = Uri.EscapeDataString($"{t.Album} {TrackText.PrimaryArtist(t.Artist)}");
        (ok, body) = await FetchAsync(
            new Uri($"https://itunes.apple.com/search?term={albumTerm}&media=music&entity=album&limit=10&country={country}")).ConfigureAwait(false);
        if (!ok) return (null, true);

        var album = ItunesMatcher.Parse(body!)
            .Where(a => TrackText.Similarity(a.CollectionName, t.Album) > 0 &&
                        (TrackText.Similarity(a.ArtistName, t.Artist) > 0 ||
                         TrackText.ArtistParts(t.Artist).Any(p => TrackText.Similarity(a.ArtistName, p) > 0)))
            .OrderByDescending(a => TrackText.Similarity(a.CollectionName, t.Album))
            .FirstOrDefault();
        return (album is null ? null : new ItunesSong(null, album.ArtistName, album.CollectionName,
            album.ArtworkUrl100, null, album.CollectionViewUrl, album.ArtistViewUrl), false);
    }

    private async Task<ArtistEntry?> GetArtistAsync(NowPlaying track, TrackEntry? song)
    {
        // Candidates, most specific first: the full credit, the first credited artist, iTunes' spelling.
        var candidates = new[] { track.Artist, TrackText.PrimaryArtist(track.Artist), song?.ItunesArtistName }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in candidates)
        {
            var key = TrackText.Normalize(name);
            if (key.Length == 0) continue;
            if (TryGetCached(_cache.Artists, key, out var cached))
            {
                if (cached.Found) return cached;
                continue;
            }

            var (ok, body) = await FetchAsync(
                new Uri($"https://api.deezer.com/search/artist?q={Uri.EscapeDataString(name)}&limit=5")).ConfigureAwait(false);
            if (!ok) return null;

            var entry = ParseDeezer(body!, key);
            if (entry is null)
            {
                Store(_cache.Artists, key, new ArtistEntry { Found = false });
                continue;
            }
            Store(_cache.Artists, key, entry);
            return entry;
        }
        return null;
    }

    /// <summary>Takes the Deezer artist whose name matches exactly (normalized); a "close" artist would show a stranger's face.</summary>
    private static ArtistEntry? ParseDeezer(string json, string normalizedName)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

        foreach (var a in data.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (TrackText.Normalize(name) != normalizedName) continue;

            var picture = a.TryGetProperty("picture_xl", out var p) ? p.GetString() : null;
            // Deezer serves a blank placeholder ("…/artist//1000x1000…") for artists without a photo.
            if (string.IsNullOrWhiteSpace(picture) || picture.Contains("/artist//")) continue;

            return new ArtistEntry
            {
                Found = true,
                ImageUrl = picture,
                Url = a.TryGetProperty("link", out var l) ? l.GetString() : null,
            };
        }
        return null;
    }

    // ---------------------------------------------------------------- HTTP

    /// <summary>GET with retry on transient failures (timeouts, network errors, 429, 5xx). ok=false means "couldn't get an answer".</summary>
    private async Task<(bool Ok, string? Body)> FetchAsync(Uri uri)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(uri).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return (true, await response.Content.ReadAsStringAsync().ConfigureAwait(false));

                var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!transient)
                {
                    Log.Warn($"{uri.Host} answered {(int)response.StatusCode}; giving up on this lookup.");
                    return (false, null);
                }
                Log.Debug($"{uri.Host} answered {(int)response.StatusCode} (attempt {attempt + 1}).");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                Log.Debug($"{uri.Host} request failed (attempt {attempt + 1}): {ex.Message}");
            }

            if (attempt >= _config.MaxRetries) return (false, null);
            await Task.Delay(_retryBaseDelay * Math.Pow(2, attempt)).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- cache

    private static string TrackKey(NowPlaying t) =>
        $"{TrackText.Normalize(t.Artist)}|{TrackText.Normalize(t.Title)}|{TrackText.Normalize(t.Album)}";

    private bool TryGetCached<T>(Dictionary<string, T> map, string key, out T entry) where T : CacheEntry
    {
        lock (_cacheLock)
        {
            if (map.TryGetValue(key, out var found) && found.ExpiresAt > _clock.GetUtcNow())
            {
                entry = found;
                return true;
            }
        }
        entry = null!;
        return false;
    }

    private void Store<T>(Dictionary<string, T> map, string key, T entry) where T : CacheEntry
    {
        entry.ExpiresAt = _clock.GetUtcNow() + (entry.Found ? TimeSpan.FromDays(_config.CacheDays) : NotFoundTtl);
        lock (_cacheLock)
        {
            map[key] = entry;
            Prune(map);
            SaveCacheLocked();
        }
    }

    private void Prune<T>(Dictionary<string, T> map) where T : CacheEntry
    {
        if (map.Count <= MaxCacheEntries) return;
        var now = _clock.GetUtcNow();
        foreach (var k in map.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList()) map.Remove(k);
        foreach (var k in map.OrderBy(kv => kv.Value.ExpiresAt).Take(map.Count - MaxCacheEntries).Select(kv => kv.Key).ToList()) map.Remove(k);
    }

    private void LoadCache()
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return;
        try
        {
            _cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath)) ?? new CacheFile();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("Artwork cache unreadable, starting fresh.", ex);
            _cache = new CacheFile();
        }
    }

    private void SaveCacheLocked()
    {
        if (_cachePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache));
            File.Move(tmp, _cachePath, overwrite: true);   // atomic swap: a crash can't leave a half-written cache
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not save the artwork cache.", ex);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // ---------------------------------------------------------------- cache types

    public abstract class CacheEntry
    {
        public bool Found { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    public sealed class TrackEntry : CacheEntry
    {
        public string? AlbumArtUrl { get; set; }
        public string? TrackUrl { get; set; }
        public string? AlbumUrl { get; set; }
        public string? ArtistUrl { get; set; }
        public string? ItunesArtistName { get; set; }
    }

    public sealed class ArtistEntry : CacheEntry
    {
        public string? ImageUrl { get; set; }
        public string? Url { get; set; }
    }

    public sealed class CacheFile
    {
        public Dictionary<string, TrackEntry> Tracks { get; set; } = new();
        public Dictionary<string, ArtistEntry> Artists { get; set; } = new();
    }
}
