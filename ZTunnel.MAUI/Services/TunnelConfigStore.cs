using System.Text.Json;
using ZTunnel.MAUI.Models;

namespace ZTunnel.MAUI.Services;

/// <summary>
/// Persists the list of SSH hosts (each with their own credentials + port forwards)
/// to a local JSON file on disk.
/// </summary>
public class TunnelConfigStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public List<SshHost> Hosts { get; private set; } = new();

    public TunnelConfigStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZTunnel");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "config.json");
        Load();
    }

    private record ConfigDto(List<SshHost> Hosts);

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var dto = JsonSerializer.Deserialize<ConfigDto>(json);
                    Hosts = dto?.Hosts ?? new List<SshHost>();
                }
            }
            catch
            {
                // ignore corrupt config
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            var dto = new ConfigDto(Hosts);
            var json = JsonSerializer.Serialize(dto,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
    }

    public SshHost? GetHost(Guid id) => Hosts.FirstOrDefault(h => h.Id == id);

    public void AddHost(SshHost host)
    {
        Hosts.Add(host);
        Save();
    }

    public void RemoveHost(Guid id)
    {
        Hosts.RemoveAll(h => h.Id == id);
        Save();
    }

    public void UpdateHost(SshHost host)
    {
        var idx = Hosts.FindIndex(x => x.Id == host.Id);
        if (idx >= 0) Hosts[idx] = host;
        Save();
    }

    public void AddForward(Guid hostId, PortForward fwd)
    {
        var host = GetHost(hostId);
        if (host == null) return;
        host.Forwards.Add(fwd);
        Save();
    }

    public void RemoveForward(Guid hostId, Guid forwardId)
    {
        var host = GetHost(hostId);
        if (host == null) return;
        host.Forwards.RemoveAll(f => f.Id == forwardId);
        Save();
    }

    public void UpdateForward(Guid hostId, PortForward fwd)
    {
        var host = GetHost(hostId);
        if (host == null) return;
        var idx = host.Forwards.FindIndex(x => x.Id == fwd.Id);
        if (idx >= 0) host.Forwards[idx] = fwd;
        Save();
    }
}
