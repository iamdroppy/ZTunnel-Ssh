using Renci.SshNet;
using ZTunnel.Models;

namespace ZTunnel.Services;

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

    public string Status { get; set; } = "Disconnected";
    public string? LastError { get; set; }
    public DateTime? ConnectedSince { get; set; }

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
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            client.ErrorOccurred += (_, args) =>
            {
                rt.LastError = args.Exception.Message;
                rt.Status = "Error";
                RaiseChanged();
            };

            client.Connect();
            rt.Client = client;

            foreach (var fwd in host.Forwards.Where(f => f.Enabled))
                AddPortInternal(rt, fwd);

            rt.Status = "Connected";
            rt.ConnectedSince = DateTime.Now;
            RaiseChanged();

            rt.Cts = new CancellationTokenSource();
            var token = rt.Cts.Token;
            rt.Loop = Task.Run(() => MonitorLoopAsync(rt.HostId, token));
        }
        catch (Exception ex)
        {
            rt.LastError = ex.Message;
            rt.Status = "Error";
            RaiseChanged();
            _logger.LogWarning(ex, "Failed to connect host {HostId}", hostId);
        }
        finally
        {
            rt.Gate.Release();
        }
    }

    private async Task MonitorLoopAsync(Guid hostId, CancellationToken ct)
    {
        var rt = GetRuntime(hostId);
        try
        {
            while (!ct.IsCancellationRequested && !_stopping.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                if (rt.Client is { IsConnected: false })
                {
                    rt.Status = "Reconnecting";
                    rt.LastError = "Connection dropped";
                    RaiseChanged();
                    await Task.Delay(2000, ct);
                    if (ct.IsCancellationRequested) return;
                    _ = ConnectHostAsync(hostId);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task DisconnectHostAsync(Guid hostId)
    {
        var rt = GetRuntime(hostId);
        var host = _store.GetHost(hostId);
        await rt.Gate.WaitAsync();
        try
        {
            rt.Cts.Cancel();
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
