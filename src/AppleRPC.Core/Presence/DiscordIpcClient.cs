// SPDX-License-Identifier: AGPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppleRPC.Logging;

namespace AppleRPC.Presence;

/// <summary>Discord answered a command with an error (or refused the handshake).</summary>
public sealed class DiscordRpcException(int code, string message) : Exception($"Discord RPC error {code}: {message}")
{
    public int Code { get; } = code;
}

/// <summary>
/// Talks to the local Discord client over its IPC named pipe (\\.\pipe\discord-ipc-0 … 9).
/// Wire format: each frame is [int32 opcode LE][int32 length LE][UTF-8 JSON].
/// Opcodes: 0 HANDSHAKE, 1 FRAME, 2 CLOSE, 3 PING, 4 PONG.
/// </summary>
public sealed class DiscordIpcClient : IAsyncDisposable
{
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    private const int MaxFrameBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep "⭐" and accents readable
    };

    private readonly string _clientId;
    private readonly string _pipeNameFormat;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private NamedPipeClientStream? _pipe;
    private Task? _readLoop;
    private volatile bool _connected;
    private int _disconnectRaised;

    public bool IsConnected => _connected;
    public string? UserName { get; private set; }

    /// <summary>Raised once when the connection is lost (Discord closed / crashed / pipe broke).</summary>
    public event Action? Disconnected;

    /// <param name="pipeNameFormat">"discord-ipc-{0}" in production; tests use a private name.</param>
    public DiscordIpcClient(string clientId, string pipeNameFormat = "discord-ipc-{0}")
    {
        _clientId = clientId;
        _pipeNameFormat = pipeNameFormat;
    }

    /// <summary>Finds a running Discord (tries pipes 0-9), performs the handshake and waits for READY.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        NamedPipeClientStream? pipe = null;
        for (var i = 0; i < 10 && pipe is null; i++)
        {
            var candidate = new NamedPipeClientStream(".", string.Format(_pipeNameFormat, i), PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await candidate.ConnectAsync(150, ct).ConfigureAwait(false);
                pipe = candidate;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
        }
        if (pipe is null) throw new IOException("Discord is not running (no IPC pipe found).");

        _pipe = pipe;
        var ready = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending["READY"] = ready;
        _readLoop = Task.Run(() => ReadLoopAsync(pipe));

        try
        {
            await WriteFrameAsync(OpHandshake, JsonSerializer.Serialize(new { v = 1, client_id = _clientId }), ct).ConfigureAwait(false);
            var data = await ready.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);

            if (data.TryGetProperty("user", out var user))
            {
                UserName = (user.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String ? gn.GetString() : null)
                           ?? (user.TryGetProperty("username", out var un) ? un.GetString() : null);
            }
            _connected = true;
            Log.Info($"Connected to Discord{(UserName is null ? "" : $" as {UserName}")}.");
        }
        catch
        {
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sets the activity, or clears it when <paramref name="activity"/> is null. Throws <see cref="DiscordRpcException"/> if Discord rejects it.</summary>
    public async Task SetActivityAsync(ActivityPayload? activity, CancellationToken ct = default)
    {
        if (!_connected) throw new IOException("Not connected to Discord.");

        var nonce = Guid.NewGuid().ToString();
        var request = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce,
        }, JsonOptions);

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        try
        {
            Log.Debug($"-> SET_ACTIVITY {request}");
            await WriteFrameAsync(OpFrame, request, ct).ConfigureAwait(false);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IOException("Discord did not answer SET_ACTIVITY in time.");
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    // ---------------------------------------------------------------- I/O

    private async Task WriteFrameAsync(int opcode, string json, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new IOException("Not connected to Discord.");
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(frame, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            throw new IOException("The Discord connection is closed.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        var header = new byte[8];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(header, _cts.Token).ConfigureAwait(false);
                var opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
                if (length < 0 || length > MaxFrameBytes) throw new IOException($"Absurd frame length {length}.");

                var body = new byte[length];
                await pipe.ReadExactlyAsync(body, _cts.Token).ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(body);

                switch (opcode)
                {
                    case OpPing:
                        await WriteFrameAsync(OpPong, json, _cts.Token).ConfigureAwait(false);
                        break;
                    case OpClose:
                        HandleClose(json);
                        return;
                    case OpFrame:
                        HandleFrame(json);
                        break;
                    default:
                        Log.Debug($"Ignoring IPC opcode {opcode}.");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            Log.Debug($"Discord IPC read loop ended: {ex.Message}");
        }
        finally
        {
            _connected = false;
            FailPending(new IOException("The Discord connection was lost."));
            RaiseDisconnected();
        }
    }

    private void HandleFrame(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var evt = root.TryGetProperty("evt", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;

        if (evt == "READY")
        {
            if (_pending.TryRemove("READY", out var ready)) ready.TrySetResult(data);
            return;
        }

        var nonce = root.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (nonce is null || !_pending.TryGetValue(nonce, out var tcs)) return;

        if (evt == "ERROR")
        {
            var code = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : -1;
            var message = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            tcs.TrySetException(new DiscordRpcException(code, message));
        }
        else
        {
            tcs.TrySetResult(data);
        }
    }

    /// <summary>Opcode 2: Discord refused us (e.g. {"code":4000,"message":"Invalid Client ID"}).</summary>
    private void HandleClose(string json)
    {
        var code = -1;
        var message = "Discord closed the connection.";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci)) code = ci;
            if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
        }
        catch (JsonException) { /* keep the defaults */ }

        FailPending(new DiscordRpcException(code, message));
    }

    private void FailPending(Exception ex)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(ex);
    }

    private void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0) Disconnected?.Invoke();
    }

    private async Task CloseAsync()
    {
        _connected = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _pipe?.Dispose(); } catch { /* already gone */ }
        if (_readLoop is not null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* best effort */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
