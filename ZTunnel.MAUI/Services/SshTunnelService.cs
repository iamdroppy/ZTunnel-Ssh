using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using ZTunnel.MAUI.Models;

namespace ZTunnel.MAUI.Services;

/// <summary>
/// Runtime state for a single SSH host. Holds the live SshClient, the active
/// forwarded-port registry, and the cancellation plumbing for the host's
/// monitor loop.
/// </summary>
public class HostRuntime
{
    public Guid HostId { get; }
    public SshClient? Client { get; set; }
    public Dictionary<Guid, ForwardedPort> ActiveForwards { get; } = new();
    public Task? Loop { get; set; }
    public CancellationTokenSource Cts { get; set; } = new();
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// Fires whenever we detect a disconnect/error — lets the monitor loop
    /// wake up instantly instead of waiting for the next poll tick.
    /// </summary>
    public SemaphoreSlim DropSignal { get; } = new(0, int.MaxValue);

    public string Status { get; set; } = "Disconnected";
    public string? LastError { get; set; }
    public DateTime? ConnectedSince { get; set; }

    /// <summary>Should the monitor keep reconnecting if the session drops?</summary>
    public bool KeepAlive { get; set; }

    public HostRuntime(Guid hostId) { HostId = hostId; }
}

/// <summary>
/// Singleton background service that manages one long-lived SSH.NET session
/// per configured host. Supports per-host connect / disconnect / reconnect,
/// and live add / remove of port forwards without tearing down the session.
/// </summary>
public class SshTunnelService : BackgroundService
{
    private readonly TunnelConfigStore _store;
    private readonly ILogger<SshTunnelService> _logger;
    private readonly Dictionary<Guid, HostRuntime> _runtimes = new();
    private CancellationToken _stopping;

    public event Action? StateChanged;
    private void RaiseChanged() => StateChanged?.Invoke();

    public SshTunnelService(TunnelConfigStore store, ILogger<SshTunnelService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public HostRuntime GetRuntime(Guid hostId)
    {
        lock (_runtimes)
        {
            if (!_runtimes.TryGetValue(hostId, out var rt))
            {
                rt = new HostRuntime(hostId);
                _runtimes[hostId] = rt;
            }
            return rt;
        }
    }

    public bool IsConnected(Guid hostId) => GetRuntime(hostId).Client?.IsConnected ?? false;

    public int ConnectedCount
    {
        get
        {
            lock (_runtimes)
                return _runtimes.Values.Count(r => r.Client?.IsConnected == true);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;

        // Auto-connect any hosts that are marked AutoConnect.
        foreach (var host in _store.Hosts.Where(h => h.AutoConnect && h.IsValid))
        {
            _ = ConnectHostAsync(host.Id);
        }

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        lock (_runtimes) ids = _runtimes.Keys.ToList();
        foreach (var id in ids)
            await DisconnectHostAsync(id);
        await base.StopAsync(cancellationToken);
    }

    public async Task ConnectHostAsync(Guid hostId)
    {
        var host = _store.GetHost(hostId);
        if (host == null || !host.IsValid) return;

        var rt = GetRuntime(hostId);
        rt.KeepAlive = true;

        // Start (or restart) a single monitor loop for this host. The loop
        // owns the actual connect calls so reconnect attempts after a drop
        // happen instantly and keep retrying until cancelled.
        if (rt.Loop is null || rt.Loop.IsCompleted)
        {
            rt.Cts = new CancellationTokenSource();
            var token = rt.Cts.Token;
            rt.Loop = Task.Run(() => MonitorLoopAsync(rt.HostId, token));
        }
        else
        {
            // Loop already running — wake it up so it (re)attempts immediately.
            try { rt.DropSignal.Release(); } catch { }
        }

        await Task.Yield();
    }

    /// <summary>
    /// Performs the actual connect. Only called from inside MonitorLoopAsync.
    /// </summary>
    private async Task<bool> TryConnectOnceAsync(Guid hostId)
    {
        var host = _store.GetHost(hostId);
        if (host == null || !host.IsValid) return false;

        var rt = GetRuntime(hostId);
        await rt.Gate.WaitAsync();
        try
        {
            DisconnectInternal(rt, host);

            rt.Status = "Connecting";
            rt.LastError = null;
            RaiseChanged();

            var info = new Renci.SshNet.ConnectionInfo(host.Host, host.Port, host.Username,
                new PasswordAuthenticationMethod(host.Username, host.Password))
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            var client = new SshClient(info);
            client.KeepAliveInterval = TimeSpan.FromSeconds(15);
            client.ErrorOccurred += (_, args) =>
            {
                rt.LastError = args.Exception.Message;
                rt.Status = "Error";
                RaiseChanged();
                // Wake monitor loop immediately — do not wait for next poll.
                try { rt.DropSignal.Release(); } catch { }
            };

            client.Connect();
            rt.Client = client;

            foreach (var fwd in host.Forwards.Where(f => f.Enabled))
                AddPortInternal(rt, fwd);

            rt.Status = "Connected";
            rt.ConnectedSince = DateTime.Now;
            RaiseChanged();
            return true;
        }
        catch (Exception ex)
        {
            rt.LastError = ex.Message;
            rt.Status = "Error";
            RaiseChanged();
            _logger.LogWarning(ex, "Failed to connect host {HostId}", hostId);
            return false;
        }
        finally
        {
            rt.Gate.Release();
        }
    }

    /// <summary>
    /// Long-running per-host loop. Establishes the initial connection,
    /// then polls every 200 ms (and wakes instantly on ErrorOccurred)
    /// to reconnect the moment the session drops. Never gives up while
    /// KeepAlive is true; uses short bounded backoff on repeated failure.
    /// </summary>
    private async Task MonitorLoopAsync(Guid hostId, CancellationToken ct)
    {
        var rt = GetRuntime(hostId);
        int consecutiveFailures = 0;
        // Tick counter for the forward supervisor — runs roughly every 5s
        // (25 * 200ms) while the SSH session is healthy.
        int forwardTick = 0;

        try
        {
            // Initial connect attempt, retry quickly on failure.
            while (!ct.IsCancellationRequested && !_stopping.IsCancellationRequested && rt.KeepAlive)
            {
                if (await TryConnectOnceAsync(hostId))
                {
                    consecutiveFailures = 0;
                    break;
                }

                consecutiveFailures++;
                var delay = BackoffMs(consecutiveFailures);
                rt.Status = "Reconnecting";
                RaiseChanged();
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            }

            // Steady-state: detect drops and reconnect immediately.
            while (!ct.IsCancellationRequested && !_stopping.IsCancellationRequested && rt.KeepAlive)
            {
                // Wait up to 200ms for a drop signal, or just poll after 200ms.
                try { await rt.DropSignal.WaitAsync(TimeSpan.FromMilliseconds(200), ct); }
                catch (OperationCanceledException) { return; }

                if (!rt.KeepAlive || ct.IsCancellationRequested) return;

                var alive = rt.Client is { IsConnected: true };
                if (alive)
                {
                    consecutiveFailures = 0;
                    // Periodically reconcile port forwards: anything enabled
                    // that isn't currently running gets (re)started, anything
                    // running that's been disabled gets stopped. This is what
                    // makes "Enabled" tick == "stay online forever".
                    if (++forwardTick >= 25)
                    {
                        forwardTick = 0;
                        await SuperviseForwardsAsync(rt);
                    }
                    continue;
                }

                rt.Status = "Reconnecting";
                rt.LastError ??= "Connection dropped";
                RaiseChanged();

                // Try to reconnect immediately, no artificial delay.
                if (await TryConnectOnceAsync(hostId))
                {
                    consecutiveFailures = 0;
                    forwardTick = 0;
                    continue;
                }

                consecutiveFailures++;
                var delay = BackoffMs(consecutiveFailures);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitor loop crashed for host {HostId}", hostId);
            rt.Status = "Error";
            rt.LastError = ex.Message;
            RaiseChanged();
        }
    }

    /// <summary>
    /// Bounded exponential-ish backoff in milliseconds. First retry is
    /// effectively immediate; it caps at 5 seconds so reconnect stays snappy.
    /// </summary>
    private static int BackoffMs(int failures) => failures switch
    {
        <= 1 => 0,
        2    => 250,
        3    => 500,
        4    => 1000,
        5    => 2000,
        _    => 5000,
    };

    public async Task DisconnectHostAsync(Guid hostId)
    {
        var rt = GetRuntime(hostId);
        var host = _store.GetHost(hostId);

        // Signal the monitor to stop reconnecting first.
        rt.KeepAlive = false;
        try { rt.Cts.Cancel(); } catch { }
        try { rt.DropSignal.Release(); } catch { }

        await rt.Gate.WaitAsync();
        try
        {
            DisconnectInternal(rt, host);
            rt.Status = "Disconnected";
            rt.ConnectedSince = null;
            RaiseChanged();
        }
        finally
        {
            rt.Gate.Release();
        }
    }

    public async Task ReconnectHostAsync(Guid hostId)
    {
        var rt = GetRuntime(hostId);
        // Kick the existing monitor into a fresh attempt without tearing
        // the loop down — this is what "reconnect immediately" means.
        if (rt.Loop is not null && !rt.Loop.IsCompleted && rt.KeepAlive)
        {
            await rt.Gate.WaitAsync();
            try
            {
                DisconnectInternal(rt, _store.GetHost(hostId));
                rt.Status = "Reconnecting";
                RaiseChanged();
            }
            finally { rt.Gate.Release(); }

            try { rt.DropSignal.Release(); } catch { }
            return;
        }

        await DisconnectHostAsync(hostId);
        await ConnectHostAsync(hostId);
    }

    private void DisconnectInternal(HostRuntime rt, SshHost? host)
    {
        foreach (var port in rt.ActiveForwards.Values)
        {
            try { if (port.IsStarted) port.Stop(); } catch { }
            try { port.Dispose(); } catch { }
        }
        rt.ActiveForwards.Clear();

        if (host != null)
            foreach (var f in host.Forwards) f.Status = "Stopped";

        if (rt.Client is not null)
        {
            try { if (rt.Client.IsConnected) rt.Client.Disconnect(); } catch { }
            try { rt.Client.Dispose(); } catch { }
            rt.Client = null;
        }
    }

    private void AddPortInternal(HostRuntime rt, PortForward fwd)
    {
        if (rt.Client is null || !rt.Client.IsConnected) return;
        try
        {
            var bp = (uint)fwd.BoundPort;
            var dp = (uint)fwd.DestinationPort;
            ForwardedPort port = fwd.Direction == ForwardDirection.Local
                ? new ForwardedPortLocal(fwd.BoundHost, bp, fwd.DestinationHost, dp)
                : new ForwardedPortRemote(fwd.BoundHost, bp, fwd.DestinationHost, dp);

            port.Exception += (_, args) =>
            {
                fwd.LastError = args.Exception.Message;
                fwd.Status = "Error";
                RaiseChanged();
            };

            rt.Client.AddForwardedPort(port);
            port.Start();
            rt.ActiveForwards[fwd.Id] = port;
            fwd.Status = "Running";
            fwd.LastError = null;
        }
        catch (Exception ex)
        {
            fwd.Status = "Error";
            fwd.LastError = ex.Message;
        }
    }

    /// <summary>
    /// Reconciles the live forward set against each forward's Enabled flag.
    /// Called periodically by the monitor loop while the SSH session is up.
    ///   - Enabled + not running  → (re)start it.
    ///   - Disabled + running     → stop it.
    /// Failures here are non-fatal; the next supervisor tick will retry.
    /// </summary>
    private async Task SuperviseForwardsAsync(HostRuntime rt)
    {
        var host = _store.GetHost(rt.HostId);
        if (host == null) return;
        if (rt.Client is not { IsConnected: true }) return;

        await rt.Gate.WaitAsync();
        try
        {
            // Re-check after taking the gate — a reconnect may have just run.
            if (rt.Client is not { IsConnected: true }) return;

            bool changed = false;

            foreach (var fwd in host.Forwards)
            {
                rt.ActiveForwards.TryGetValue(fwd.Id, out var existing);
                var running = existing is { IsStarted: true };

                if (!fwd.Enabled)
                {
                    // User unticked Enabled — tear it down if it's still up.
                    if (existing != null)
                    {
                        try { if (existing.IsStarted) existing.Stop(); } catch { }
                        try { existing.Dispose(); } catch { }
                        rt.ActiveForwards.Remove(fwd.Id);
                        fwd.Status = "Stopped";
                        fwd.LastError = null;
                        changed = true;
                    }
                    continue;
                }

                if (running) continue;

                // Enabled but not running — clear any stale entry and try again.
                if (existing != null)
                {
                    try { if (existing.IsStarted) existing.Stop(); } catch { }
                    try { existing.Dispose(); } catch { }
                    rt.ActiveForwards.Remove(fwd.Id);
                }

                AddPortInternal(rt, fwd);
                changed = true;
            }

            if (changed) RaiseChanged();
        }
        finally
        {
            rt.Gate.Release();
        }
    }

    /// <summary>Adds a forward to the live session (no reconnect).</summary>
    public async Task AddLiveForwardAsync(Guid hostId, PortForward fwd)
    {
        var rt = GetRuntime(hostId);
        await rt.Gate.WaitAsync();
        try
        {
            if (rt.Client is { IsConnected: true } && fwd.Enabled)
            {
                AddPortInternal(rt, fwd);
                RaiseChanged();
            }
        }
        finally { rt.Gate.Release(); }
    }

    /// <summary>Stops and removes a live port forward (no reconnect).</summary>
    public async Task RemoveLiveForwardAsync(Guid hostId, Guid forwardId)
    {
        var rt = GetRuntime(hostId);
        await rt.Gate.WaitAsync();
        try
        {
            if (rt.ActiveForwards.TryGetValue(forwardId, out var port))
            {
                try { if (port.IsStarted) port.Stop(); } catch { }
                try { port.Dispose(); } catch { }
                rt.ActiveForwards.Remove(forwardId);
            }
            var f = _store.GetHost(hostId)?.Forwards.FirstOrDefault(x => x.Id == forwardId);
            if (f != null) f.Status = "Stopped";
            RaiseChanged();
        }
        finally { rt.Gate.Release(); }
    }
}
