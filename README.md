# BeaconServer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blueviolet.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)](#prerequisites)
[![Game](https://img.shields.io/badge/Game-Subnautica_2-darkgreen.svg)](https://store.steampowered.com/app/1962700/)

[简体中文](README.zh.md)

Open-source dedicated-server supervisor for **Subnautica 2** Beacon multiplayer. Pairs with the (closed-source) Beacon launcher to give Subnautica 2 a real IP/port dedicated server: standard Unreal listen-server transport, save snapshots and rollback, Source A2S query, Source RCON, HMAC-signed HTTP admin API, and a Lua/C++ mod surface.

The Beacon **launcher** is closed-source and distributed as a binary; this **server** is fully open-source and MIT licensed. Both ship together at [HumanGenome/Beacon](https://github.com/HumanGenome/Beacon).

_Beacon is a community project and is not affiliated with or endorsed by the developers of Subnautica 2._

> **Official Hosting:** [SurvivalServers.com Subnautica 2 hosting](https://www.survivalservers.com/games/subnautica_2/?utm_source=github&utm_medium=readme&utm_campaign=beaconserver) offers Subnautica 2 servers with Beacon pre-installed and managed.

---

## Table of Contents

- [Features](#features)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Running BeaconServer](#running-beaconserver)
- [Admin & RCON](#admin--rcon)
- [Mods](#mods)
- [Build from source](#build-from-source)
- [License](#license)
- [Credits](#credits)

---

## Features

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

To self-host:

1. Download `Beacon-Server-Windows-x64-<version>.zip` from the [latest release](https://github.com/HumanGenome/BeaconServer/releases/latest) (or from the umbrella [HumanGenome/Beacon](https://github.com/HumanGenome/Beacon/releases/latest)).
2. Extract somewhere on the Windows machine that will host the server (e.g. `C:\Beacon\`).
3. Open `appsettings.json` and set:
   - `Beacon:SnInstallRoot` — full path to your Subnautica 2 install (the folder with `Subnautica2.exe`)
   - `Beacon:RconPassword` — a strong password. This is also Beacon's HTTP admin key.
   - `Beacon:GameplayPort` — the UDP port players will connect to (default `7777`)
4. Forward / open these ports on your firewall + router:
   - `<GameplayPort>` UDP — gameplay
   - `<GameplayPort> + 2` UDP — server query (A2S)
   - `<GameplayPort> + 3` TCP — RCON (optional)
   - `<GameplayPort> + 4` TCP — admin HTTP API (optional)
5. Run `BeaconServer.exe`. The console prints the listen address; that's what players join with the Beacon launcher.

---

## Running BeaconServer

```
BeaconServer.exe
```

That's it — it stays in the foreground and tails its own console output. Logs are written to `logs/beaconserver-<date>.ndjson`. Players connect via the Beacon launcher to `<HostAddress>:<GameplayPort>`.

To run as a Windows service, wrap it with NSSM or `sc.exe create`. The supervisor process owns the SN2 lifecycle, so a stop is graceful: it tells the in-process plugin to flush a final snapshot, then terminates the SN2 process.

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

Or use the GitHub Actions release workflow — tag a `vX.Y.Z` and watch the pipeline produce `Beacon-Server-Windows-x64-vX.Y.Z.zip` and cross-publish to the umbrella [Beacon](https://github.com/HumanGenome/Beacon/releases) release page.

---

## Contributing

Issues and pull requests welcome. For bug reports, include the BeaconServer version (`status` over RCON, or `/api/v1/health`), the Subnautica 2 build ID, and the relevant logs (`logs/beaconserver-*.ndjson`).

See [CONTRIBUTING.md](CONTRIBUTING.md).

---

## License

MIT. See [LICENSE](LICENSE).

## Credits

- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) — Unreal Engine scripting and modding framework
