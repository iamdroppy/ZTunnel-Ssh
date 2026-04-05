# ZTunnel

A Blazor Web App (.NET 10) that creates and manages SSH TCP port-forwarding
tunnels using **SSH.NET** (`Renci.SshNet`) running as a background hosted service.

## Features

- Username / password SSH authentication.
- Multiple TCP port forwards (Local `-L` and Remote `-R`) with per-entry enable/disable.
- Persistent config in `%LOCALAPPDATA%\ZTunnel\config.json`.
- Background `IHostedService` that keeps the SSH connection alive and
  auto-reconnects if it drops.
- Add / edit / remove forwards live without disconnecting, or force a full
  reconnect at any time.
- Responsive dark UI (sidebar + top bar + cards), modal editor, live status chips.
- Built-in "Server Setup Guide" page.

## Pages

| Path          | What it does                                     |
|---------------|--------------------------------------------------|
| `/`           | Dashboard — connection status, active tunnels.   |
| `/credentials`| Configure SSH host, port, user, password.        |
| `/tunnels`    | Add / edit / remove / enable port forwards.      |
| `/setup`      | Tutorial for configuring sshd for TCP tunneling. |

## Run

```bash
cd ZTunnel
dotnet restore
dotnet run
```

Then open https://localhost:7080 (or http://localhost:5080).

1. Go to **SSH Credentials**, fill in host/port/user/password, click **Save & Reconnect**.
2. Go to **Port Forwards**, click **+ Add forward**, configure bound + destination host/port, save.
3. Toggle forwards on/off, or click **Reconnect SSH** to rebuild the session.

## How it works

- `SshTunnelService : BackgroundService` owns a single `SshClient` and a
  dictionary of active `ForwardedPortLocal` / `ForwardedPortRemote` instances.
- Its main loop idles on a `CancellationTokenSource` that is fired whenever
  the UI calls `ReconnectAsync()`.
- `AddLiveForwardAsync` / `RemoveLiveForwardAsync` mutate the live client
  without reconnecting, so you can add or remove ports on the fly.
- `TunnelConfigStore` reads/writes the JSON config on disk and holds the
  in-memory state shared with the UI.

## Requirements

- .NET 10 SDK
- An SSH server reachable from the machine running ZTunnel, with
  `AllowTcpForwarding yes` (see `/setup` inside the app).
