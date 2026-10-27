# BeaconServer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blueviolet.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)](#requirements)
[![Game](https://img.shields.io/badge/Game-Subnautica_2-darkgreen.svg)](https://store.steampowered.com/app/1962700/)

**Subnautica 2** has no dedicated server. BeaconServer runs it as a Subnautica 2 dedicated server: it starts the game headless on a Windows machine with a public IP and port, keeps Beacon's in-game runtime loaded, and players join by address through the [Beacon app](https://github.com/HumanGenome/Beacon). It also takes and restores save snapshots, answers Source A2S query, runs Source RCON and serves an admin HTTP API.

A playable host needs Beacon's in-game runtime (a `ue4ss\` folder with Beacon's server mods, plus the native `Beacon.dll`) next to the `BeaconServer\` folder. The **release zip bundles this runtime**, so a downloaded server is complete. Building from this source yourself produces the supervisor only; see [Installation](#installation).

## Features

### 🖥 Host supervision
Starts Subnautica 2 with the Beacon runtime, monitors the game process, tracks the plugin heartbeat, and coordinates restarts.

### 💾 Save snapshots
Snapshots the world automatically on every game auto-save (when `SnapshotsEnabled` is on) and on admin trigger. Restore swaps the save directory atomically so a failed restore does not leave a half-written world, and a pre-restore snapshot is taken first so the operation is reversible.

### 📡 Source query
Answers standard Source A2S query on the configured query port so monitoring tools can read server name, map, player count, and the player list.

### 🛠 Source RCON
Runs a Source-compatible RCON listener with `help`, `status`, `players`, `ping`, `save snapshot`, `save list`, `say`, `announce`, and `motd`, plus slash commands registered by server mods.

### 🔐 Admin HTTP API
Exposes snapshot list/upload/download/restore, save import, health, player list, mod manifest, chat, and live-map endpoints. Admin routes are HMAC-signed with a key derived from the RCON password.

### 🧩 Mod surface
Loads UE4SS Lua and C++ mods through the Beacon runtime layout, and publishes the server's mod manifest at `GET /api/v1/manifest` for the launcher to install on join.

## Requirements

- Windows 10/11 or Windows Server x64
- Subnautica 2 game files installed on the host machine (BeaconServer launches them; it does not ship the game)
- Open/forwarded ports for gameplay, query, RCON, and admin HTTP as needed

Release builds are self-contained; a separate .NET install is not required for normal use.

## Installation

### Rent a server
[SurvivalServers.com Subnautica 2 hosting](https://www.survivalservers.com/services/game_servers/subnautica_2/?utm_source=github&utm_medium=readme_install&utm_campaign=beaconserver) ships the complete Beacon server runtime already installed and handles ports, updates, and panel integration.

### Self-host
1. Download `Beacon-Server-Windows-x64-v<version>.zip` from the [latest release](https://github.com/HumanGenome/BeaconServer/releases/latest). It is self-contained: `BeaconServer\` (the supervisor + `appsettings.json`), `ue4ss\` and `Beacon.dll` (the in-game runtime), and `tools\` (the injector).
2. Extract it to a stable folder, such as `C:\Beacon\`.
3. Install the Subnautica 2 game files under the folder set as `SnInstallRoot` (default `C:\Beacon\game`): copy your `steamapps\common\Subnautica2` folder there, or install with SteamCMD (app `1962700`). The server runs headless; no GPU is required.
4. Edit `BeaconServer\appsettings.json` (see below).
5. Open/forward the ports listed below.
6. Run `BeaconServer\BeaconServer.exe`.

Players connect with the Beacon launcher to `<host>:<GameplayPort>`.

> **Note:** the release zip above is complete. It bundles the in-game runtime (the `ue4ss\` folder with UE4SS + Beacon's server mods, plus the native `Beacon.dll`) alongside the MIT-licensed supervisor. If you instead build BeaconServer from this source, you get the supervisor only; the runtime must then sit next to the `BeaconServer\` folder, or BeaconServer logs an error and the game runs as a plain Subnautica 2 listen server (no password gate, roster, live map, or chat slash commands). A rented Survival Servers server includes the runtime.

> **Use the latest release.** A run of older release pages carried a zip that held only the supervisor, with no `ue4ss\` folder and no `Beacon.dll`. Extracting one of those gives a host that players connect to and bounce straight back to the main menu from. A complete bundle is around 120 MB and contains `ue4ss\Mods\`; anything near 50 MB is the broken artifact. Affected pages are marked at the top of their release notes.

## Server Settings

BeaconServer reads `appsettings.json` (next to `BeaconServer.exe`) under the `Beacon` section.

| Setting | Default | Purpose |
|---|---:|---|
| `InstanceId` | `default` | Stable instance name used in logs, query rules, and API responses. |
| `ServerName` | empty | Public name shown in the launcher and Source query. Empty falls back to the instance id. |
| `SnInstallRoot` | `C:\Beacon\game` | Subnautica 2 install folder. Beacon auto-detects Steam/Epic Win64 and Xbox WinGDK layouts under this root. |
| `SnExecutablePath` | empty | Optional direct path to the Subnautica 2 executable, for example an Xbox install's `Subnautica2-WinGDK-Shipping.exe`. |
| `SnUserDir` | `C:\Beacon\userdir` | User directory used by the hosted game process; the live save lives under `Saved\SaveGames`. |
| `SaveDir` | `C:\Beacon\saves` | Snapshot zips and archived saves. |
| `GameplayPort` | `27015` | UDP port players join through Beacon. |
| `BeaconControlPort` | `27016` | Reserved (the gameplay+1 slot of the per-instance port block). |
| `QueryPort` | `27017` | UDP Source A2S query port. |
| `RconPort` | `27018` | TCP Source RCON port. RCON is disabled when `RconPassword` is empty. |
| `HttpPort` | `27019` | TCP admin HTTP API port. Set to `0` to disable. Also requires `RconPassword` to be set. |
| `RconPassword` | empty | Admin password for RCON; the HTTP API signing key is derived from it. Set this before exposing RCON or HTTP. |
| `ServerPassword` | empty | **Legacy, ignored since v0.3.55.** Do not set; use `BeaconAuthPassword`. |
| `BeaconAuthPassword` | empty | Join password enforced server-side for remote players. Empty means an open server. Also sets the password flag in Source query. |
| `Admins` | `[]` | Identities (SteamID64, Beacon user id, or display name) treated as admin when running mod slash commands from in-game chat. RCON callers are always admin. |
| `MaxPlayers` | `4` | Slot count reported to the launcher and query clients. |
| `SnapshotsEnabled` | `true` | Auto-snapshot on every game auto-save. When `false`, only admin-triggered snapshots run. |
| `MaxUploadBytes` | 2 GB | Size cap for snapshot uploads and save imports over the HTTP API. |
| `PluginHeartbeatTimeoutSeconds` | `30` | Seconds before BeaconServer treats the game runtime as unresponsive. |
| `Chat:Enabled` | `true` | Filters the mod manifest only: when `false`, a `beacon-hud` entry in `Mods` is left out of `GET /api/v1/manifest`, so joining players do not install a chat overlay. It does not add an overlay, and the chat API stays up either way. |
| `Mods` | empty | Mod manifest published at `GET /api/v1/manifest`: `Required`, `Recommended`, and `Blocked` lists. Re-read on edit; no restart needed. |
| `Map:Enabled` | `true` | Live web map served at `/map/` on the admin HTTP port. |
| `Map:Public` | `false` | When `true`, `/api/v1/map/state` is readable without auth, so the browser map shows live players to anyone with the URL. |

**In-game chat is not included in a self-hosted server.** Players get a chat window in the game only when the server's `Mods` list names Beacon's chat overlay (`beacon-hud`), which the Beacon app then installs when they join. The overlay is not published for self-hosting, so a server set up from this release has no in-game chat window. The RCON and HTTP chat commands (`say`, `announce`, `motd`, `/api/v1/chat/*`) still run and keep the recent messages, but nothing in the game shows them.

Keep the ports unique for each server instance. The standard layout is:

| Port | Protocol | Purpose |
|---:|---|---|
| `GameplayPort` | UDP | Subnautica 2 gameplay |
| `GameplayPort + 2` | UDP | Source A2S query |
| `GameplayPort + 3` | TCP | Source RCON |
| `GameplayPort + 4` | TCP | Admin HTTP API |

The launcher derives the query, RCON and admin HTTP ports as gameplay+2, gameplay+3 and gameplay+4, so keep those offsets if you want the launcher's status, Console and world tools to work against your server.

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

Connect to `RconPort` with the configured `RconPassword` (RCON is disabled while the password is empty).

```text
help
status
players
ping
save snapshot
save list
say <message>
announce <message>
motd [message]
```

Mod-registered slash commands also run over RCON, with or without the leading `/`. Restoring a snapshot is **not** an RCON command; use the launcher's World backups dialog or the HTTP API.

See [docs/ADMIN.md](docs/ADMIN.md) for exact command output, the HTTP API signing recipe, and the endpoint list.

## Build From Source

```powershell
git clone https://github.com/HumanGenome/BeaconServer.git
cd BeaconServer
dotnet build BeaconServer.sln -c Release
dotnet test BeaconServer.sln -c Release --no-build
dotnet publish src/server/BeaconServer/BeaconServer.csproj -c Release -r win-x64 --self-contained true
```

Published output lands under `src/server/BeaconServer/bin/Release/net8.0/win-x64/publish/`.

## Known Issues

### "The game was not started via the platform launcher and will be closed."
Subnautica 2 asks the Steam client to confirm the copy of the game it is running. On a headless host that answer often does not come back, and the game closes itself with exit code 0, which leaves BeaconServer relaunching it in a loop.

Three things have to be true on the server machine:

- The Steam client is running and signed in to an account that owns Subnautica 2. Offline mode is fine.
- Steam is signed in as the same Windows user, in the same session, as the one running BeaconServer. A Steam client in another session is not visible to the game.
- `steam_appid.txt` containing the single line `1962700` sits next to `Subnautica2-Win64-Shipping.exe`.

From v0.3.125 BeaconServer writes `steam_appid.txt` itself on every launch and passes the same id to the game through the environment, so the part left to you is the Steam client. If the game still closes itself, BeaconServer now says so in its log and stops relaunching instead of looping.

Tracked at [HumanGenome/Beacon#8](https://github.com/HumanGenome/Beacon/issues/8).

### Game build must match between client and server
After a Subnautica 2 update, players on the new build can fail to join an older server, or join and then get disconnected when they reach something the update changed. Update the server's game files whenever the game updates.

## Community Note

Beacon is a community project and is not affiliated with or endorsed by the developers of Subnautica 2.

## Contributing

Report bugs on [Beacon's issue tracker](https://github.com/HumanGenome/Beacon/issues); this repo has issues turned off. Include the BeaconServer version, the Subnautica 2 build, and the relevant lines from `logs\beacon-*.log` (one JSON object per line, rolled daily). From v0.3.130 that log masks join passwords; check an older log for `Password=` before posting it. Pull requests are welcome here.

## License

MIT. See [LICENSE](LICENSE).

## Credits

- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS): Unreal Engine scripting and modding framework
