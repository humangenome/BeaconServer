# BeaconServer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blueviolet.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)](#prerequisites)
[![Game](https://img.shields.io/badge/Game-Subnautica_2-darkgreen.svg)](https://store.steampowered.com/app/1962700/)

Open-source host supervisor for **Subnautica 2** Beacon multiplayer. BeaconServer is the server-side process manager and admin API, not the full Beacon product by itself. A working Beacon server also relies on the closed-source **Beacon client-side launcher** and game-side runtime shipped in the full Beacon release bundle.

Together, Beacon gives Subnautica 2 a real IP/port multiplayer server: standard Unreal listen-server transport, save snapshots and rollback, Source A2S query, Source RCON, HMAC-signed HTTP admin API, and a UE4SS mod surface. BeaconServer is fully open-source and MIT licensed; the launcher and game-side runtime are proprietary binaries distributed from [HumanGenome/Beacon](https://github.com/HumanGenome/Beacon).

_Beacon is a community project and is not affiliated with or endorsed by the developers of Subnautica 2._

> **Official Hosting:** [SurvivalServers.com Subnautica 2 hosting](https://www.survivalservers.com/games/subnautica_2/?utm_source=github&utm_medium=readme&utm_campaign=beaconserver) offers Subnautica 2 servers with Beacon pre-installed and managed.

---

## Table of Contents

- [Features](#features)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Server settings](#server-settings)
- [Running BeaconServer](#running-beaconserver)
- [Source query example](#source-query-example)
- [Admin & RCON](#admin--rcon)
- [Mods](#mods)
- [Build from source](#build-from-source)
- [Component split](#component-split)
- [License](#license)
- [Credits](#credits)

---

## Features

### Requires Beacon
BeaconServer expects players to join with the Beacon launcher. The launcher patches the local client session and loads the client-side runtime needed to connect by IP and port. The full host bundle also includes Beacon's game-side runtime for the server process. Running the server-only zip without those runtime components is useful for source builds and tests, but it will not create a complete playable host.

### Subnautica 2 supervisor
BeaconServer manages the Subnautica 2 game process: starts it under the right launch URL, monitors it via a named-pipe IPC channel from the in-process plugin, restarts it on crashes, and gates restarts during snapshot restores.

### Save snapshots & atomic rollback
The world is snapshotted on a schedule and on admin trigger. Restore is atomic — the live save is swapped at the filesystem level, not patched in place — so a mid-restore failure can't leave a half-applied world.

### Source A2S query
Server browsers, monitoring tools, and Discord status bots can see your server's name, map, player count, and uptime. Standard A2S, no special client required.

### Source RCON
Standard Source RCON listener on `gameplay port + 3`. Works with `mcrcon`, BattleMetrics, and any standard RCON client.

### HMAC-signed admin HTTP API
HTTP API on `gameplay port + 4` for snapshot, restore, world transfer, and player roster, signed with `HMAC_SHA256(SHA256(RconPassword), ...)`. 5-minute replay window with anti-replay tracking. Body upload streams to a temp file with chunked SHA256 so large uploads don't triple-allocate in memory.

### Mod surface
Lua and C++ mods run server-side through UE4SS. Drop a mod folder under `ue4ss\Mods\` and it loads automatically.

---

## Prerequisites

- **Windows 10 or 11** (64-bit) — BeaconServer is a self-contained Windows binary
- **Subnautica 2** installed (Steam, Epic Games, or Microsoft Store) on the same machine
- A UDP port for gameplay (default `7777`) and its derived ports for query/RCON/HTTP

You do **not** need a separate .NET install — release artifacts are self-contained.

---

## Installation

The easy way is managed hosting from [SurvivalServers.com](https://www.survivalservers.com/games/subnautica_2/?utm_source=github&utm_medium=readme_install&utm_campaign=beaconserver) — BeaconServer comes pre-installed and they handle ports, snapshots, and updates.

To self-host a playable server:

1. Download `Beacon-Bundle-Windows-x64-v<version>.zip` from the [HumanGenome/Beacon latest release](https://github.com/HumanGenome/Beacon/releases/latest).
2. Extract somewhere on the Windows machine that will host the server (e.g. `C:\Beacon\`).
3. Open `BeaconServer\appsettings.json` and set:
   - `Beacon:SnInstallRoot` — full path to your Subnautica 2 install (the folder with `Subnautica2.exe`)
   - `Beacon:RconPassword` — a strong password. This is also Beacon's HTTP admin key.
   - `Beacon:ServerPassword` — optional password players must enter before joining.
   - `Beacon:GameplayPort` — the UDP port players will connect to (default `7777`)
4. Forward / open these ports on your firewall + router:
   - `<GameplayPort>` UDP — gameplay
   - `<GameplayPort> + 2` UDP — server query (A2S)
   - `<GameplayPort> + 3` TCP — RCON (optional)
   - `<GameplayPort> + 4` TCP — admin HTTP API (optional)
5. Run `BeaconServer\BeaconServer.exe`. The console prints the listen address; that's what players join with the Beacon launcher.

The `Beacon-Server-Windows-x64-v<version>.zip` artifact contains only the MIT BeaconServer binaries. It is useful for source users, tests, and custom packaging, but by itself it does not include the Beacon launcher, game-side runtime, or UE4SS layout required for a connectable Subnautica 2 host.

---

## Server settings

BeaconServer reads settings from `BeaconServer\appsettings.json` under the `Beacon` section.

| Setting | Default | Purpose |
|---|---:|---|
| `InstanceId` | `default` | Stable name for this server instance. Also used in logs and generated defaults. |
| `ServerName` | empty | Public name shown in Beacon and Source A2S query. Empty falls back to `Beacon - <InstanceId>`. |
| `SnInstallRoot` | `C:\Beacon\game` | Subnautica 2 install folder containing `Subnautica2.exe`. |
| `SnUserDir` | `C:\Beacon\userdir` | User data directory used by the hosted game process. |
| `SaveDir` | `C:\Beacon\saves` | Beacon snapshot and save metadata directory. |
| `GameplayPort` | `27015` | UDP port players join through Beacon. |
| `QueryPort` | `27017` | UDP Source A2S query port for server lists and monitoring. |
| `RconPort` | `27018` | TCP Source RCON port. RCON stays disabled when `RconPassword` is empty. |
| `HttpPort` | `27019` | TCP admin HTTP API port. Set to `0` to disable. |
| `RconPassword` | empty | RCON password and admin HTTP signing secret. Set this before exposing RCON or HTTP. |
| `ServerPassword` | empty | Optional join password players enter in Beacon. |
| `MaxPlayers` | `4` | Slot count reported to Beacon and A2S. |
| `SnapshotsEnabled` | `true` | Enables automatic save snapshots and restore support. |
| `PluginHeartbeatTimeoutSeconds` | `30` | Time BeaconServer waits before treating the game-side runtime as unresponsive. |

Keep `GameplayPort`, `QueryPort`, `RconPort`, and `HttpPort` unique per server instance. A common layout is `27015` gameplay, `27017` query, `27018` RCON, and `27019` HTTP.

---

## Running BeaconServer

```
BeaconServer.exe
```

That's it — it stays in the foreground and tails its own console output. Logs are written to `logs/beaconserver-<date>.ndjson`. Players connect via the Beacon launcher to `<HostAddress>:<GameplayPort>`.

To run as a Windows service, wrap it with NSSM or `sc.exe create`. The supervisor process owns the SN2 lifecycle, so a stop is graceful: it tells the in-process plugin to flush a final snapshot, then terminates the SN2 process.

---

## Source query example

BeaconServer answers standard Source A2S queries on `QueryPort`, so existing monitoring tools can read the server name, map, player count, and player list.

Quick test with Python:

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

The same `QueryPort` also works with generic Source/Valve query clients such as GameDig, LGSM monitors, and server browser bots.

---

## Admin & RCON

See [docs/ADMIN.md](docs/ADMIN.md) for the full guide:

- RCON command reference (`status`, `players`, `ping`, `save snapshot`, `save list`)
- Admin HTTP API endpoints (`/api/v1/health`, `/info`, `/snapshots`, `/snapshots/{id}/restore`, etc.) with HMAC signing recipe
- Snapshot lifecycle and restore mechanics

---

## Mods

Lua and C++ mods load via UE4SS on the server. See [docs/MODS.md](docs/MODS.md) for entry points, the host API, and a worked example.

---

## Build from source

Requires Windows + .NET 8 SDK.

```
git clone https://github.com/HumanGenome/BeaconServer.git
cd BeaconServer
dotnet build BeaconServer.sln -c Release
dotnet test BeaconServer.sln -c Release --no-build
dotnet publish src/server/BeaconServer/BeaconServer.csproj -c Release -r win-x64 --self-contained true
```

Published output lands at `src/server/BeaconServer/bin/Release/net8.0/win-x64/publish/`.

Or use the GitHub Actions release workflow — tag a `vX.Y.Z` and watch the pipeline produce the server-only `Beacon-Server-Windows-x64-vX.Y.Z.zip`.

## Component split

BeaconServer is MIT licensed and buildable from this repository. A working Beacon deployment also needs:

- **Beacon Launcher** — the closed-source client-side launcher every player uses to add servers and launch Subnautica 2 into the Beacon session.
- **Beacon game-side runtime** — the closed-source runtime components loaded into the client/server game processes.
- **Beacon full bundle** — the release artifact that combines BeaconServer, the game-side runtime, UE4SS layout, and helper tools for self-hosting.

Use the full bundle on the [Beacon release page](https://github.com/HumanGenome/Beacon/releases/latest) for playable self-host installs. Do not redistribute or commercially repackage the proprietary Beacon launcher/runtime binaries without permission.

---

## Contributing

Issues and pull requests welcome. For bug reports, include the BeaconServer version (`status` over RCON, or `/api/v1/health`), the Subnautica 2 build ID, and the relevant logs (`logs/beaconserver-*.ndjson`).

See [CONTRIBUTING.md](CONTRIBUTING.md).

---

## License

MIT. See [LICENSE](LICENSE).

## Credits

- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) — Unreal Engine scripting and modding framework
