using System.Text.Json.Serialization;

namespace ZTunnel.MAUI.Models;

public enum ForwardDirection
{
    Local,   // -L: bind local -> remote (accessed from local machine)
    Remote   // -R: bind remote -> local
}

public class PortForward
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ForwardDirection Direction { get; set; } = ForwardDirection.Remote;

    public string BoundHost { get; set; } = "127.0.0.1";
    public int BoundPort { get; set; } = 8080;

    public string DestinationHost { get; set; } = "127.0.0.1";
    public int DestinationPort { get; set; } = 80;

    public bool Enabled { get; set; } = true;

    [JsonIgnore] public string Status { get; set; } = "Stopped";
    [JsonIgnore] public string? LastError { get; set; }

    public string DisplayRoute => Direction == ForwardDirection.Local
        ? $"local {BoundHost}:{BoundPort} → remote {DestinationHost}:{DestinationPort}"
        : $"remote {BoundHost}:{BoundPort} → local {DestinationHost}:{DestinationPort}";
}
