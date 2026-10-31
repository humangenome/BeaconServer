<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/HumanGenome/Beacon/main/docs/img/beacon-logo-dark.png">
    <img src="https://raw.githubusercontent.com/HumanGenome/Beacon/main/docs/img/beacon-logo-light.png" alt="Beacon" width="480">
  </picture>
</p>

<p align="center">
  <a href="#what-you-need"><img src="https://img.shields.io/badge/Platform-Windows_x64-blue.svg" alt="Platform"></a>
  <a href="https://store.steampowered.com/app/1962700/"><img src="https://img.shields.io/badge/Game-Subnautica_2-darkgreen.svg" alt="Game"></a>
  <a href="https://github.com/HumanGenome/Beacon"><img src="https://img.shields.io/badge/Player_App-Beacon-444.svg" alt="Player App"></a>
  <a href="https://dotnet.microsoft.com/en-us/download/dotnet/10.0"><img src="https://img.shields.io/badge/.NET-10.0-blueviolet.svg" alt=".NET 10"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License"></a>
</p>

# BeaconServer

BeaconServer runs a **Subnautica 2 dedicated server** on Windows. Players join by IP and port with the [Beacon app](https://github.com/HumanGenome/Beacon).

**Subnautica 2** has no dedicated server of its own. BeaconServer starts the game headless on a machine with a public IP and port and keeps Beacon's in-game runtime loaded. The [release zip](https://github.com/HumanGenome/BeaconServer/releases/latest) bundles that runtime; building this source yourself produces the supervisor only (see [Build from source](#build-from-source)).

## What it does

### ♻️ Crash restarts
Starts Subnautica 2 with the Beacon runtime and starts it again if the game exits. If the game exits within a minute six times in a row, BeaconServer stops and says why in its log instead of looping.

### 💾 World backups
Takes a snapshot of the world on every game auto-save (when `SnapshotsEnabled` is on) and on request. A restore takes a snapshot of the running world first, then swaps the save folder in one step, so a failed restore does not leave a half-written world.

### 📡 Server query
Answers Source A2S query, so monitoring tools can read the server's name, map, player count and player list.

### 🖥 Server console
Source RCON with `help`, `status`, `players`, `ping`, `save snapshot`, `save list`, `say`, `announce` and `motd`, plus slash commands registered by server mods.

### 🔐 Admin API
An HTTP API for world backups, save import, health, the player list, the mod list, chat and the live map. Admin routes are signed with a key derived from the RCON password.

### 🧩 Mods
Loads UE4SS mods through the Beacon runtime, and publishes the server's mod list at `GET /api/v1/manifest` for the Beacon app to install when a player joins. See [docs/MODS.md](docs/MODS.md).

## What you need

- Windows 10, 11 or Server, 64-bit
- Subnautica 2 game files on the machine (BeaconServer starts them; it does not ship the game)
- No GPU, and no separate .NET install: release builds are self-contained

## Set it up

1. Download `Beacon-Server-Windows-x64-v<version>.zip` from the [latest release](https://github.com/HumanGenome/BeaconServer/releases/latest). It holds `BeaconServer\` (the supervisor and `appsettings.json`), `ue4ss\` and `Beacon.dll` (the in-game runtime) and `tools\` (the injector).
2. Extract it to a stable folder, such as `C:\Beacon\`.
3. Install the Subnautica 2 game files under the folder set as `SnInstallRoot` (default `C:\Beacon\game`): copy your `steamapps\common\Subnautica2` folder there, or install with SteamCMD (app `1962700`).
4. Edit `BeaconServer\appsettings.json` (see [Settings](#settings)).
5. Open the ports listed under [Ports](#ports).
6. Run `BeaconServer\BeaconServer.exe`.

Players connect with the Beacon app to `<host>:<GameplayPort>`.

> **Use the latest release.** A run of older release pages carried a zip that held only the supervisor, with no `ue4ss\` folder and no `Beacon.dll`. Players connect to one of those and bounce straight back to the main menu. A complete bundle is around 120 MB and contains `ue4ss\Mods\`; anything near 50 MB is the broken one. Affected pages say so at the top of their release notes.

> **Building from this source?** You get the supervisor only. The runtime must sit next to the `BeaconServer\` folder, or BeaconServer logs an error and the game runs as a plain Subnautica 2 listen server (no password, player list, live map or chat slash commands).

## Ports

Keep the ports unique for each server on a machine. The Beacon app works out the query, RCON and admin ports from the gameplay port, so keep these offsets if you want its status, Console and World windows to work.

| Port | Protocol | Purpose |
|---:|---|---|
| `GameplayPort` | UDP | Subnautica 2 gameplay |
| `GameplayPort + 2` | UDP | Source A2S query |
| `GameplayPort + 3` | TCP | Source RCON |
| `GameplayPort + 4` | TCP | Admin HTTP API and the live map |

## Settings

BeaconServer reads `appsettings.json` (next to `BeaconServer.exe`) under the `Beacon` section.

| Setting | Default | Purpose |
|---|---:|---|
| `InstanceId` | `default` | Stable instance name used in logs, query rules and API responses. |
| `ServerName` | empty | Public name shown in the Beacon app and Source query. Empty falls back to the instance id. |
| `SnInstallRoot` | `C:\Beacon\game` | Subnautica 2 install folder. Beacon detects Steam/Epic Win64 and Xbox WinGDK layouts under this root. |
| `SnExecutablePath` | empty | Optional direct path to the Subnautica 2 executable, for example an Xbox install's `Subnautica2-WinGDK-Shipping.exe`. |
| `SnUserDir` | `C:\Beacon\userdir` | User folder of the hosted game; the live save lives under `Saved\SaveGames`. |
| `SaveDir` | `C:\Beacon\saves` | Snapshot zips and archived saves. |
| `GameplayPort` | `27015` | UDP port players join through. |
| `BeaconControlPort` | `27016` | Reserved (the gameplay+1 slot of the port block). |
| `QueryPort` | `27017` | UDP Source A2S query port. |
| `RconPort` | `27018` | TCP Source RCON port. RCON is off while `RconPassword` is empty. |
| `HttpPort` | `27019` | TCP admin HTTP API port. `0` turns it off; it also needs `RconPassword`. |
| `RconPassword` | empty | Admin password for RCON; the HTTP API signing key comes from it. Set this before exposing RCON or HTTP. |
| `ServerPassword` | empty | **Legacy, ignored since v0.3.55.** Use `BeaconAuthPassword`. |
| `BeaconAuthPassword` | empty | Join password for remote players. Empty means an open server. Also sets the password flag in Source query. |
| `Admins` | `[]` | SteamID64s, Beacon user ids or display names treated as admin when running mod slash commands from in-game chat. RCON callers are always admin. |
| `MaxPlayers` | `4` | Slot count reported to the app and query clients. |
| `SnapshotsEnabled` | `true` | Snapshot on every game auto-save. When `false`, only snapshots you ask for run. |
| `MaxUploadBytes` | 2 GB | Size cap for snapshot uploads and save imports over the HTTP API. |
| `PluginHeartbeatTimeoutSeconds` | `30` | Seconds before BeaconServer treats the game runtime as unresponsive. |
| `Chat:Enabled` | `true` | Filters the mod list only: when `false`, a `beacon-hud` entry in `Mods` is left out of `GET /api/v1/manifest`, so joining players do not install a chat overlay. It does not add an overlay, and the chat API stays up either way. |
| `Mods` | empty | Mod list published at `GET /api/v1/manifest`: `Required`, `Recommended` and `Blocked`. Re-read on edit; no restart needed. |
| `Map:Enabled` | `true` | Live web map served at `/map/` on the admin HTTP port. |
| `Map:Public` | `false` | When `true`, `/api/v1/map/state` is readable without auth, so the browser map shows live players to anyone with the URL. |

**In-game chat is not included in a self-hosted server.** Players get a chat window in the game only when the server's `Mods` list names Beacon's chat overlay (`beacon-hud`), which the Beacon app then installs when they join. The overlay is not published for self-hosting, so a server set up from this release has no in-game chat window. The RCON and HTTP chat commands (`say`, `announce`, `motd`, `/api/v1/chat/*`) still run and keep the recent messages, but nothing in the game shows them.

## Source query example

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

The same port works with tools such as GameDig, LGSM monitors and Discord status bots that support Source query.

## RCON

Connect to `RconPort` with the configured `RconPassword`.

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

Mod-registered slash commands also run over RCON, with or without the leading `/`. Restoring a snapshot is **not** an RCON command; use the Beacon app's World backups window or the HTTP API.

See [docs/ADMIN.md](docs/ADMIN.md) for exact command output, the HTTP API signing recipe and the endpoint list.

## Rent a server

[SurvivalServers.com Subnautica 2 hosting](https://www.survivalservers.com/services/game_servers/subnautica_2/?utm_source=github&utm_medium=readme_install&utm_campaign=beaconserver) ships the complete Beacon server runtime already installed and handles ports, updates and panel integration.

## Build from source

```powershell
git clone https://github.com/HumanGenome/BeaconServer.git
cd BeaconServer
dotnet build BeaconServer.sln -c Release
dotnet test BeaconServer.sln -c Release --no-build
dotnet publish src/server/BeaconServer/BeaconServer.csproj -c Release -r win-x64 --self-contained true
```

Published output lands under `src/server/BeaconServer/bin/Release/net10.0/win-x64/publish/`.

## Known issues

### "The game was not started via the platform launcher and will be closed."
Subnautica 2 asks the Steam client to confirm the copy of the game it is running. On a headless host that answer often does not come back, and the game closes itself with exit code 0.

Three things have to be true on the server machine:

- The Steam client is running and signed in to an account that owns Subnautica 2. Offline mode is fine.
- Steam is signed in as the same Windows user, in the same session, as the one running BeaconServer. A Steam client in another session is not visible to the game.
- `steam_appid.txt` containing the single line `1962700` sits next to `Subnautica2-Win64-Shipping.exe`.

From v0.3.125 BeaconServer writes `steam_appid.txt` itself on every launch and passes the same id to the game through the environment, so the part left to you is the Steam client. If the game still closes itself, BeaconServer says so in its log and stops relaunching.

Tracked at [HumanGenome/Beacon#8](https://github.com/HumanGenome/Beacon/issues/8).

### Game build must match between client and server
After a Subnautica 2 update, players on the new build can fail to join an older server, or join and then get disconnected when they reach something the update changed. Update the server's game files whenever the game updates.

## Releases

Each release holds the complete server zip (`Beacon-Server-Windows-x64-v<version>.zip`) and `checksums.txt`. The player app is published from [HumanGenome/Beacon](https://github.com/HumanGenome/Beacon/releases/latest).

## Contributing

Report bugs on [Beacon's issue tracker](https://github.com/HumanGenome/Beacon/issues); this repo has issues turned off. Include the BeaconServer version, the Subnautica 2 build, and the relevant lines from `logs\beacon-*.log` (one JSON object per line, rolled daily). From v0.3.130 that log masks join passwords; check an older log for `Password=` before posting it. Pull requests are welcome here.

## Community note

Beacon is a community project and is not affiliated with or endorsed by the developers of Subnautica 2.

## License

MIT. See [LICENSE](LICENSE).

## Credits

- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS): Unreal Engine scripting and modding framework
