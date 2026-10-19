// SPDX-License-Identifier: AGPL-3.0-only
using AppleRPC.Configuration;
using AppleRPC.Logging;

namespace AppleRPC.Presence;

public enum PresenceState { Disconnected, Connected }

/// <summary>
/// Owns the Discord connection and decides <i>when</i> an activity is actually sent.
/// <list type="bullet">
/// <item>Latest wins: <see cref="SetActivity"/> only records the desired state and returns immediately.</item>
/// <item>Debounce: at most one update per <c>MinUpdateIntervalSeconds</c> (Discord's 15 s limit); when several changes pile up, only the newest is sent.</item>
/// <item>Dedupe: nothing is sent if the activity would look identical to the one already shown.</item>
/// <item>Self-healing: if Discord isn't running or restarts, it reconnects and re-sends the current state.</item>
/// </list>
/// </summary>
public sealed class PresenceService : IAsyncDisposable
{
    private readonly Config _config;
    private readonly Func<DiscordIpcClient> _clientFactory;
    private readonly TimeSpan _minInterval;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _fatalRetryDelay;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();

    // Written by SetActivity (under _gate), read by the worker.
    private ActivityPayload? _desired;
    private long _desiredVersion;
    private long _handledVersion;

    // Worker-only state.
    private DiscordIpcClient? _client;
    private ActivityPayload? _lastSent;
    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;
    private Task? _worker;

    public PresenceState State { get; private set; } = PresenceState.Disconnected;
    public string? DiscordUser { get; private set; }

    /// <summary>Last connection / Discord problem worth showing to the user (null = all good).</summary>
    public string? LastError { get; private set; }

    public event Action? StateChanged;

    public PresenceService(Config config, Func<DiscordIpcClient>? clientFactory = null,
        TimeSpan? retryDelay = null, TimeSpan? minInterval = null)
    {
        _config = config;
        _clientFactory = clientFactory ?? (() => new DiscordIpcClient(config.DiscordApplicationId));
        _minInterval = minInterval ?? TimeSpan.FromSeconds(config.MinUpdateIntervalSeconds);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(10);
        _fatalRetryDelay = retryDelay ?? TimeSpan.FromSeconds(60);
    }

    public void Start() => _worker ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Request a new presence; <c>null</c> clears it. Never blocks and never throws.</summary>
    public void SetActivity(ActivityPayload? activity)
    {
        lock (_gate)
        {
            _desired = activity;
            _desiredVersion++;
        }
        try { _wake.Release(); } catch (ObjectDisposedException) { }
    }

    // ---------------------------------------------------------------- worker

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested && HasPendingWork())
                    await ApplyLatestAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Log.Error("Presence worker crashed.", ex);
        }
    }

    private bool HasPendingWork()
    {
        lock (_gate) return _desiredVersion != _handledVersion;
    }

    private (ActivityPayload? Activity, long Version) Snapshot()
    {
        lock (_gate) return (_desired, _desiredVersion);
    }

    private async Task ApplyLatestAsync(CancellationToken ct)
    {
        // 1. Respect the rate limit. Anything requested while we wait is picked up by the re-read below.
        var wait = _lastSentAt + _minInterval - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            Log.Debug($"Rate limit: waiting {wait.TotalSeconds:0.0}s before the next presence update.");
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        var (desired, version) = Snapshot();
        var connected = _client is { IsConnected: true };

        // 2. Nothing to do?
        if (desired is null && !connected)
        {
            MarkHandled(version);   // not connected = nothing displayed, nothing to clear
            return;
        }
        if (connected && SameAsShown(desired))
        {
            MarkHandled(version);
            return;
        }

        // 3. Make sure we're connected (keeps retrying while the same state is wanted).
        if (!connected)
        {
            if (!await TryConnectAsync(ct).ConfigureAwait(false)) return;   // handled version unchanged -> loop retries
        }

        // 4. Send.
        try
        {
            await _client!.SetActivityAsync(desired, ct).ConfigureAwait(false);
            _lastSent = desired;
            _lastSentAt = DateTimeOffset.UtcNow;
            LastError = null;
            Log.Info(desired is null
                ? "Presence cleared."
                : $"Presence updated: {desired.Details} — {desired.State}{(desired.Timestamps is null ? " (no timer)" : "")}");
            MarkHandled(version);
        }
        catch (DiscordRpcException ex)
        {
            // Discord understood us but refused the payload. Retrying the same payload is pointless.
            Log.Warn($"Discord rejected the presence: {ex.Message}");
            LastError = ex.Message;
            _lastSentAt = DateTimeOffset.UtcNow;
            MarkHandled(version);
            StateChanged?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Warn($"Lost the connection to Discord while updating: {ex.Message}");
            await DropClientAsync().ConfigureAwait(false);   // loop retries with a fresh connection
        }
    }

    private bool SameAsShown(ActivityPayload? desired) =>
        desired is null ? _lastSent is null : desired.IsEquivalentTo(_lastSent);

    private void MarkHandled(long version)
    {
        lock (_gate) _handledVersion = Math.Max(_handledVersion, version);
    }

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        var client = _clientFactory();
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            var fatal = ex is DiscordRpcException;
            LastError = fatal ? ex.Message : null;
            if (fatal) Log.Error($"Discord refused the connection: {ex.Message} (check DiscordApplicationId).");
            else Log.Debug($"Discord not reachable: {ex.Message}");
            SetState(PresenceState.Disconnected);

            await Task.Delay(fatal ? _fatalRetryDelay : _retryDelay, ct).ConfigureAwait(false);
            return false;
        }

        _client = client;
        _lastSent = null;            // a fresh connection shows nothing yet
        DiscordUser = client.UserName;
        LastError = null;
        client.Disconnected += () => OnClientDisconnected(client);
        SetState(PresenceState.Connected);
        return true;
    }

    private void OnClientDisconnected(DiscordIpcClient client)
    {
        // Ignore stale clients (already replaced / disposed by us).
        if (!ReferenceEquals(client, _client) || _cts.IsCancellationRequested) return;

        Log.Info("Discord closed the connection; will reconnect when there is something to show.");
        _client = null;
        _lastSent = null;
        SetState(PresenceState.Disconnected);

        // Re-queue the current state so it is re-sent after reconnecting.
        lock (_gate)
        {
            if (_desired is not null) _desiredVersion++;
        }
        try { _wake.Release(); } catch (ObjectDisposedException) { }
    }

    private async Task DropClientAsync()
    {
        var client = _client;
        _client = null;
        _lastSent = null;
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        SetState(PresenceState.Disconnected);
    }

    private void SetState(PresenceState state)
    {
        if (State == state) { if (state == PresenceState.Disconnected) StateChanged?.Invoke(); return; }
        State = state;
        if (state != PresenceState.Connected) DiscordUser = null;
        StateChanged?.Invoke();
    }

    // ---------------------------------------------------------------- shutdown

    /// <summary>Stops the worker, clears the presence (best effort, bypasses the rate limit) and disconnects.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* best effort */ }
        }

        var client = _client;
        _client = null;
        if (client is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                if (client.IsConnected) await client.SetActivityAsync(null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug($"Could not clear the presence on exit: {ex.Message}");
            }
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _wake.Dispose();
        _cts.Dispose();
    }
}
