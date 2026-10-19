// SPDX-License-Identifier: AGPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AppleRPC.Artwork;
using AppleRPC.Configuration;
using AppleRPC.Media;

namespace AppleRPC.Tests;

internal static class T
{
    public const string AppId = "123456789012345678";

    public static Config Config(Action<Config>? tweak = null)
    {
        var c = new Config { DiscordApplicationId = AppId };
        tweak?.Invoke(c);
        c.Normalize();
        return c;
    }

    public static NowPlaying Track(
        string title = "Get Lucky", string artist = "Daft Punk", string album = "Random Access Memories",
        PlaybackStatus status = PlaybackStatus.Playing, int posSec = 30, int durSec = 369, DateTimeOffset? at = null) =>
        new(title, artist, album, status, TimeSpan.FromSeconds(posSec), TimeSpan.FromSeconds(durSec), at ?? DateTimeOffset.UtcNow);

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "applerpc-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    public const string ItunesGetLucky = """
    {"resultCount":3,"results":[
      {"wrapperType":"track","kind":"song","artistName":"Daft Punk","collectionName":"Get Lucky (Radio Edit) [feat. Pharrell Williams] - Single","trackName":"Get Lucky (Radio Edit) [feat. Pharrell Williams]",
       "artworkUrl100":"https://is1-ssl.mzstatic.com/image/thumb/Music/single/100x100bb.jpg","trackViewUrl":"https://music.apple.com/us/album/single?i=1","collectionViewUrl":"https://music.apple.com/us/album/single","artistViewUrl":"https://music.apple.com/us/artist/daft-punk/5468295"},
      {"wrapperType":"track","kind":"song","artistName":"Daft Punk","collectionName":"Random Access Memories","trackName":"Get Lucky (feat. Pharrell Williams & Nile Rodgers)",
       "artworkUrl100":"https://is1-ssl.mzstatic.com/image/thumb/Music/ram/100x100bb.jpg","trackViewUrl":"https://music.apple.com/us/album/ram?i=2","collectionViewUrl":"https://music.apple.com/us/album/ram","artistViewUrl":"https://music.apple.com/us/artist/daft-punk/5468295"},
      {"wrapperType":"track","kind":"song","artistName":"Some Cover Band","collectionName":"Get Lucky Tribute","trackName":"Get Lucky",
       "artworkUrl100":"https://is1-ssl.mzstatic.com/image/thumb/Music/cover/100x100bb.jpg","trackViewUrl":"https://music.apple.com/us/album/cover?i=3"}
    ]}
    """;

    public const string DeezerDaftPunk = """
    {"data":[{"id":27,"name":"Daft Punk","link":"https://www.deezer.com/artist/27","picture_xl":"https://cdn-images.dzcdn.net/images/artist/abc123/1000x1000-000000-80-0-0.jpg"}],"total":1}
    """;
}

/// <summary>HttpMessageHandler driven by a lambda; counts requests.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public ConcurrentQueue<string> Requests { get; } = new();
    public FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue(request.RequestUri!.AbsoluteUri);
        return Task.FromResult(_respond(request));
    }

    public int Count(string hostFragment) => Requests.Count(r => r.Contains(hostFragment));

    public static HttpResponseMessage Json(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>iTunes + Deezer both answering with the canned Daft Punk data.</summary>
    public static FakeHttp Standard() => new(req =>
        req.RequestUri!.Host.Contains("itunes") ? Json(T.ItunesGetLucky)
        : req.RequestUri.Host.Contains("deezer") ? Json(T.DeezerDaftPunk)
        : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
}

internal sealed class FakeMedia : IMediaSource
{
    public event Action<NowPlaying?>? Changed;
    public Task StartAsync() => Task.CompletedTask;
    public void Emit(NowPlaying? np) => Changed?.Invoke(np);
}

/// <summary>A stand-in for the Discord client: a named-pipe server speaking the real IPC framing.</summary>
internal sealed class FakeDiscord : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _name = "arpc" + Guid.NewGuid().ToString("N")[..10];
    private NamedPipeServerStream? _pipe;
    private Task? _loop;

    public string PipeFormat => _name + "{0}";                       // client tries ...0 .. ...9
    public ConcurrentQueue<JsonElement> Activities { get; } = new();  // every SET_ACTIVITY "activity" (Undefined/Null = clear)
    public ConcurrentQueue<string> RawRequests { get; } = new();
    public int HandshakeCount;
    public string HandshakeJson = "";
    public string? RejectActivitiesWith;       // when set, SET_ACTIVITY answers with an ERROR event
    public string? CloseHandshakeWith;         // when set, answers the handshake with op 2 (CLOSE) and this JSON

    public void Start()
    {
        _pipe = NewServer();   // listening before Start() returns, so a client can never race the server
        _loop = Task.Run(() => ServeAsync(_cts.Token));
    }

    private NamedPipeServerStream NewServer() =>
        new(_name + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    /// <summary>Abruptly hang up on the client (like Discord being closed).</summary>
    public void DropConnection() { try { _pipe?.Dispose(); } catch { } }

    private async Task ServeAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            if (!first) _pipe = NewServer();
            first = false;
            var pipe = _pipe!;
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                await HandleClientAsync(pipe, ct);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            finally { try { pipe.Dispose(); } catch { } }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (op, json) = await ReadAsync(pipe, ct);
            if (op == 0)
            {
                Interlocked.Increment(ref HandshakeCount);
                HandshakeJson = json;
                if (CloseHandshakeWith is not null) { await WriteAsync(pipe, 2, CloseHandshakeWith); return; }
                await WriteAsync(pipe, 1, """{"cmd":"DISPATCH","data":{"v":1,"user":{"id":"1","username":"tester","global_name":"Test User"}},"evt":"READY","nonce":null}""");
            }
            else if (op == 4)
            {
                Pongs.Enqueue(json);
            }
            else if (op == 1)
            {
                RawRequests.Enqueue(json);
                using var doc = JsonDocument.Parse(json);
                var nonce = doc.RootElement.GetProperty("nonce").GetString();
                var args = doc.RootElement.GetProperty("args");
                Activities.Enqueue(args.TryGetProperty("activity", out var a) ? a.Clone() : default);

                if (RejectActivitiesWith is not null)
                    await WriteAsync(pipe, 1, $$"""{"cmd":"SET_ACTIVITY","data":{"code":4000,"message":"{{RejectActivitiesWith}}"},"evt":"ERROR","nonce":"{{nonce}}"}""");
                else
                    await WriteAsync(pipe, 1, $$"""{"cmd":"SET_ACTIVITY","data":{},"evt":null,"nonce":"{{nonce}}"}""");
            }
        }
    }

    public async Task PingAsync(string payload = """{"nonce":"abc"}""")
    {
        if (_pipe is { IsConnected: true } p) await WriteAsync(p, 3, payload);
    }

    public ConcurrentQueue<string> Pongs { get; } = new();

    private static async Task<(int Op, string Json)> ReadAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[8];
        await s.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        var body = new byte[len];
        await s.ReadExactlyAsync(body, ct);
        return (op, Encoding.UTF8.GetString(body));
    }

    private static async Task WriteAsync(Stream s, int op, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);
        await s.WriteAsync(frame);
        await s.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _pipe?.Dispose(); } catch { }
        if (_loop is not null) { try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
    }
}
