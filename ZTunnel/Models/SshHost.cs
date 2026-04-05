using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ZTunnel.Models;

/// <summary>
/// Represents one SSH server with its credentials and owned port forwards.
/// </summary>
public class SshHost
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required] public string Name { get; set; } = "New SSH Host";
    [Required] public string Host { get; set; } = "";
    [Range(1, 65535)] public int Port { get; set; } = 22;
    [Required] public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public List<PortForward> Forwards { get; set; } = new();

    /// <summary>
    /// Whether this host should auto-connect when the app starts / on save.
    /// </summary>
    public bool AutoConnect { get; set; } = false;

    [JsonIgnore] public bool IsValid =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(Username) &&
        Port > 0 && Port <= 65535;
}
