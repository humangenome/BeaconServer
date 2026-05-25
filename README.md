# BeaconServer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blueviolet.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)](#requirements)
[![Game](https://img.shields.io/badge/Game-Subnautica_2-darkgreen.svg)](https://store.steampowered.com/app/1962700/)

BeaconServer is the open-source host supervisor for Beacon multiplayer in **Subnautica 2**. It starts and watches the hosted game process, tracks saves, exposes admin APIs, answers Source query, and runs RCON.

BeaconServer is not the full product by itself. Players still join with the [Beacon client app](https://github.com/HumanGenome/Beacon), and playable hosts should install the server package from the Beacon release page.

## Features

### 🖥 Host supervision
Starts Subnautica 2 with the Beacon runtime, monitors the game process, tracks plugin heartbeat, and coordinates restarts.

### 💾 Save snapshots
Creates scheduled and admin-triggered snapshots. Restore swaps the save atomically so a failed restore does not leave a half-written world.

### 📡 Source query
Answers standard Source A2S query on the configured query port so monitoring tools can read server name, map, player count, and player list.

### 🛠 Source RCON
Runs a Source-compatible RCON listener for `help`, `status`, `players`, `ping`, `save snapshot`, and `save list`.

### 🔐 Admin HTTP API
Exposes snapshot, restore, transfer, health, and roster endpoints signed with the server's admin password.

### 🧩 Mod surface
Loads UE4SS Lua and C++ mods through the Beacon runtime layout.

## Requirements

- Windows 10/11 or Windows Server x64
- Subnautica 2 installed on the host machine
- A Beacon release package from [HumanGenome/Beacon](https://github.com/HumanGenome/Beacon/releases/latest)
- Open/forwarded ports for gameplay, query, RCON, and admin HTTP as needed

Release builds are self-contained; a separate .NET install is not required for normal use.

## Installation

### Managed hosting
[SurvivalServers.com Subnautica 2 hosting](https://www.survivalservers.com/services/game_servers/subnautica_2/?utm_source=github&utm_medium=readme_install&utm_campaign=beaconserver) ships Beacon already installed and handles ports, updates, and panel integration.

### Self-host
1. Download `Beacon-Server-Windows-x64-v<version>.zip` from the [Beacon latest release](https://github.com/HumanGenome/Beacon/releases/latest).
2. Extract it somewhere stable, such as `C:\Beacon\`.
3. Edit `BeaconServer\appsettings.json`.
4. Open/forward the ports listed below.
5. Run `BeaconServer\BeaconServer.exe`.

Players connect with the Beacon client app to `<host>:<GameplayPort>`.

## Server Settings

BeaconServer reads `BeaconServer\appsettings.json` under the `Beacon` section.

| Setting | Default | Purpose |
|---|---:|---|
| `InstanceId` | `default` | Stable instance name used in logs and generated defaults. |
| `ServerName` | empty | Public name shown in Beacon and Source query. Empty falls back to the instance id. |
| `SnInstallRoot` | `C:\Beacon\game` | Subnautica 2 install folder. Beacon auto-detects Steam/Epic Win64 and Xbox WinGDK layouts under this root. |
| `SnExecutablePath` | empty | Optional direct path to the Subnautica 2 executable, for example an Xbox install's `Subnautica2-WinGDK-Shipping.exe`. |
| `SnUserDir` | `C:\Beacon\userdir` | User directory used by the hosted game process. |
| `SaveDir` | `C:\Beacon\saves` | Snapshot metadata and archived saves. |
| `GameplayPort` | `27015` | UDP port players join through Beacon. |
| `QueryPort` | `27017` | UDP Source A2S query port. |
| `RconPort` | `27018` | TCP Source RCON port. RCON is disabled when `RconPassword` is empty. |
| `HttpPort` | `27019` | TCP admin HTTP API port. Set to `0` to disable. |
| `RconPassword` | empty | Admin password for RCON and HTTP signing. Set this before exposing RCON or HTTP. |
| `ServerPassword` | empty | Optional join password players enter in Beacon. |
| `MaxPlayers` | `4` | Slot count reported to Beacon and query clients. |
| `SnapshotsEnabled` | `true` | Enables automatic save snapshots and restore support. |
| `PluginHeartbeatTimeoutSeconds` | `30` | Seconds before BeaconServer treats the game runtime as unresponsive. |

Keep the ports unique for each server instance. The standard layout is:

| Port | Protocol | Purpose |
|---:|---|---|
| `GameplayPort` | UDP | Subnautica 2 gameplay |
| `GameplayPort + 2` | UDP | Source A2S query |
| `GameplayPort + 3` | TCP | Source RCON |
| `GameplayPort + 4` | TCP | Admin HTTP API |

## Source Query Example

BeaconServer answers standard Source A2S queries on `QueryPort`.

```powershell
py -m pip install python-a2s
@'
import a2s

address = ("127.0.0.1", 27017)
info = a2s.info(address)
players = a2s.players(address)

print(f"{info.server_name} - {info.player_count}/{info.max_players} on {info.map_name}")
for player in players:
    print(f"{player.name} {player.duration:.0f}s")
'@ | py -
```

The same port works with tools such as GameDig, LGSM monitors, and Discord status bots that support Source query.

## RCON

Connect to `RconPort` with the configured `RconPassword`.

```text
status
help
players
ping
save snapshot
save list
```

See [docs/ADMIN.md](docs/ADMIN.md) for the HTTP API signing recipe and full admin endpoint list.

## Build From Source

```powershell
git clone https://github.com/HumanGenome/BeaconServer.git
cd BeaconServer
dotnet build BeaconServer.sln -c Release
dotnet test BeaconServer.sln -c Release --no-build
dotnet publish src/server/BeaconServer/BeaconServer.csproj -c Release -r win-x64 --self-contained true
```

Published output lands under `src/server/BeaconServer/bin/Release/net8.0/win-x64/publish/`.

## Community Note

Beacon is a community project and is not affiliated with or endorsed by the developers of Subnautica 2.

## Contributing

Issues and pull requests for BeaconServer are welcome. For bug reports, include the BeaconServer version, Subnautica 2 build, and relevant logs from `logs\beaconserver-*.ndjson`.

## License

MIT. See [LICENSE](LICENSE).

## Credits

- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) — Unreal Engine scripting and modding framework
