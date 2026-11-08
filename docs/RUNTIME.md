# Beacon runtime

What actually runs on a Beacon server, in what order, and which piece owns what.
Read this before changing the supervisor, the Lua stack, or the identity model.

Subnautica 2 ships no dedicated server. Beacon runs the retail game headless as a
listen host and puts a supervisor, a query/RCON/HTTP surface and a set of in-game
mods around it. Everything below follows from that one fact.

## The two processes

**BeaconServer.exe**: a .NET 10 supervisor. It owns configuration, the admin
surfaces (Source A2S query, Source RCON, an HMAC-signed HTTP API), save snapshots
and restores, and, for a self-hosted install, the game process itself. It reads
`appsettings.json` from its own folder, not from the working directory it was
started in.

**Subnautica2-Win64-Shipping.exe**: the retail game, launched headless. UE4SS is
injected through a `dwmapi.dll` proxy in the game's `Binaries\Win64\`, UE4SS loads
the Lua mod stack, and one of those mods brings `Beacon.dll` into the process.

The two talk over a local named pipe. Nothing else on the machine can reach it.

### How the game is launched

```
Subnautica2-Win64-Shipping.exe
  -USERDIR=<user dir> -unattended -port=<gameplay port> -log
  -nullrhi -NoSplash -SaveToUserDir -NoVerifyGC
  /Game/Maps/Awake?listen?LaunchType=LoadGame?SaveSlotName=savegame_0
```

- `-nullrhi` is what lets the game boot on a machine with no GPU. Without it UE
  fails to pick a D3D12 adapter and the game exits in about a second.
- The travel URL is the host trigger. `?listen` is required; a map URL without
  it comes up standalone and rejects remote clients entirely.
- `?LaunchType=LoadGame?SaveSlotName=` is the only key pair the game honours for
  loading an existing world on a listen boot. Anything else silently starts a new
  one.
- The working directory is the game's own folder, and `steam_appid.txt` plus the
  `SteamAppId` / `SteamGameId` environment variables are supplied there. The game
  asks the Steam client to confirm the copy it is running; on a headless host
  that answer only comes back when the app id is discoverable and a signed-in
  Steam client is present in the same Windows session.

`Engine.ini` in the user directory is rewritten on every launch, because the
engine wipes the user-scope file at shutdown. It sets the null online subsystem,
the IpNetDriver, the bandwidth budget, a 30 FPS cap with real frame deltas (never
a fixed timestep, which dilates game time instead of dropping frame rate), and a
silent FMOD mixer so audio init does not crash on a host with no sound device.

## Ports

Everything derives from one number, the gameplay port.

| Port | Use |
|---|---|
| `port` | Game traffic (UDP) |
| `port + 1` | Local IPC named pipe between BeaconServer and the game |
| `port + 2` | Source A2S query (UDP) |
| `port + 3` | Source RCON (TCP) |
| `port + 4` | Admin HTTP API, and the live map it serves |

Only `port`, `port + 2`, `port + 3` and `port + 4` need to be reachable from
outside. RCON is only for you. Keep the HTTP port open to players: the Beacon app
reads the server's mod list from it before it connects, and refuses the join when
it cannot.

The HTTP API signs requests with `HMAC_SHA256(SHA256(RconPassword), "METHOD\npath\ntimestamp\nbody-sha256")`,
sent as `X-Beacon-Timestamp` and `X-Beacon-Signature`. There is a five minute
replay window and seen signatures are remembered inside it. Five wrong passwords or
signatures from one address, on RCON or the API, turn that address away for a minute,
doubling up to 15 minutes. A body over 64 KB
must also carry its SHA-256 as `X-Beacon-Content-Sha256` (the signature covers
it), so the server checks the signature before reading the upload. There is no second
credential to manage: RCON and the HTTP API are the same trust tier.

## The in-game mod stack

One UE4SS mod is enabled in `mods.txt`: `BeaconServerRuntime`. It loads
everything else. Never enable the feature mods directly; load order matters and
the runtime mod is what guarantees it.

| Mod | What it does |
|---|---|
| `BeaconServerRuntime` | The single entry point. Loads `core.lua` (below), then the rest in order. |
| `BeaconModKit` | The shared Lua surface third-party mods write against. Stable API. |
| `BeaconLoader` | Brings `Beacon.dll` into the game process with `LoadLibrary` from Lua. UE4SS's own C++ loader expects an exact MSVC vtable layout and crashes the game without it. |
| `BeaconAuth` | The server password gate. Hooks post-login for incoming remote clients. Native enforcement exists in the plugin but is intentionally off because it crashes the host on spawn. |
| `BeaconRoster` | Publishes connected players to `roster.json` for the query and HTTP player lists. |
| `BeaconChat` | Chat command surface and in-game message delivery. |
| `BeaconNoPhantomHost` | Removes the listen host's own generated player from the player array, so gates and player lists see only real players. |
| `BeaconStoryGoalUnlock` | Unlocks the three chapter one lifepod story goals so the progression gate evaluates true on a host with no local player. |
| `BeaconLifepodFix` | Moves a player still stuck at the deep start 20 seconds after spawning to the surface lifepod, and takes the deep start's no-breathing effect off them (oxygen held at 0). Also removes that effect from any player who finished the lifepod ride but still has it. |
| `BeaconStarterBuilderUnlock` | Seeds per-player builder unlocks to match what a single-player start gives you. |
| `BeaconWeatherReplicationGuard` | Stops non-critical sky sequence playback from filling reliable RPC buffers on large worlds. |
| `BeaconWorldWarmup` | Keeps the spawn cells loaded so the first joiner does not arrive into a cold world. |

`core.lua` runs every module's timers and engine work on the game thread, from
one loop the runtime starts at the end of its load. UE4SS runs `LoopAsync` and
`ExecuteWithDelay` callbacks on a background thread, and engine calls from there
race the game; queueing game-thread work from that thread is not safe either.
So no server module uses them: periodic work is a `core.lua` task, and a mod
that needs the game thread uses `Beacon.GameThread.Run`. Found objects (player
controllers, the GameState, the World, the sky sequence players) are kept and
searched for again only after a map load, when one is gone, when the player
count changes, or after a few minutes; searching the whole object list on a
timer freezes the world. A "scan stats" line in `BeaconServerRuntime.log` every
5 minutes counts the searches and checks the game thread.

The roster has a redundancy worth knowing about: BeaconServer tails the game log
sub-second for join and leave lines and that is the fast path. The Lua roster
(every 5 seconds, from the game PlayerArray) is the fallback. Do not retire
either without checking the other covers every case.

### `Beacon.dll`

A C++ plugin, cross-compiled with mingw-w64 and statically linked against libgcc
and libstdc++; a dynamically linked build cannot find its runtime once it is
injected and fails to load. It hooks the game's login-options path so the host
and each joining player get a stable identity, and the rejoin path so a returning
player is matched back to the save they already have.

## Identity and saves

Subnautica 2 shards a save by the player identity it sees at login, so the
identity is what decides which world data you get back.

- The launcher writes `STEAM <steamid64> <character hash>` for the character you
  picked, or `STEAM <steamid64>` if you picked none.
- `Beacon.dll` composes the login options from that, keeping the platform user id
  as the plain Steam id for compatibility with the game's own platform code, and
  putting the composite in the field the save system hashes. Each character on one
  account therefore maps to its own save shard, which is what makes multiple
  characters on one server work.
- A host that has no client identity file (any headless host) gets a
  deterministic identity derived from its configured instance id instead. This
  path has to stay: a blank host identity poisons the early lifepod flow before
  the phantom-host prune has a chance to run.
- The rejoin path accepts both the old plain-digits external ids and the newer
  composite form, so worlds created before character selection existed still load.

### Steam identity check

Since 0.3.140 every remote join goes through `Beacon.dll`'s login check, with or
without a join password.

- Before the game starts, the app gets a Steam session ticket for Subnautica 2
  and trades it at `POST /api/v1/identity/token` for a join pass. BeaconServer
  checks the ticket with Steam through an anonymous Steam game-server sign-in and
  signs the pass with a key the running game made at start-up and keeps only in
  memory. A pass is good for 120 seconds (`Beacon:Identity:PassLifetimeSeconds`,
  60 to 180) and lets one join in: the game checks each join twice about a second
  apart, so the same pass passes once more for the same Steam ID within 10
  seconds. The route takes 6 requests per address per minute and 60 per minute
  in total.
- The login check refuses a join that claims a Steam account without a valid
  pass for that same Steam ID. A player whose app predates the check is told
  "Beacon update needed: close and reopen the Beacon app, then join again.";
  every other refusal reads "Join refused.".
- Players on the Epic or Xbox app copy join under their typed name, with no
  pass. `Beacon:BeaconNonSteamJoins` decides where: `password-servers` (the
  default) only on a server with a join password, `refuse` never, `allow` on
  every server. A name-only join never takes a save whose key starts with a
  Steam ID.
- The Steam sign-in needs Steam's client library, `steamclient64.dll`: the one
  in the folder named by `Beacon:Identity:SteamClientPath`, else one beside
  BeaconServer, else the installed Steam client's. A machine without Steam can
  get it from Valve's Steamworks redistributable (app 1007, anonymous SteamCMD).
  BeaconServer loads the game's own `steam_api64.dll`
  (`Beacon:Identity:SteamApiPath` overrides it).
- `GET /api/v1/health` reports `identity_check`: `on` once the game's login
  check, its key and the Steam sign-in are all up, otherwise `not_active`, and
  then no Steam player can get a pass. If the game's login check is not
  installed at all, `Beacon:Identity:HookFailure` decides: `stop-open-servers`
  (the default) stops the game on a server with no join password and writes
  `identity-check-stopped.txt` beside its process-id files, `warn` only logs,
  `stop-all` stops it on every server.

## Snapshots and restore

BeaconServer watches the save directory and takes snapshots on change. A restore
is not a live patch: the supervisor stops the game, swaps the save directory with
an atomic rename, moves the newest save slot to `savegame_0.sav`, re-arms the file
watcher against the new directory when `SnapshotsEnabled` is on (the old watcher
holds the renamed-away one) and relaunches. Anyone connected is disconnected for
the duration. Restore and import run only in self-hosted mode (below); in managed
mode nothing would relaunch the game, so the HTTP API refuses them with 409 and
`/api/v1/health` reports `"world_restore": false`.

## Self-hosted versus managed

The supervisor has two modes and the difference is one setting.

**Self-hosted.** `SnInstallRoot` (or `SnExecutablePath`) points at the game.
BeaconServer stages UE4SS and `Beacon.dll` into the game folder, writes
`Engine.ini` and `steam_appid.txt`, launches the game, watches it, and relaunches
it if it exits. If the game exits fast enough times in a row the supervisor gives
up rather than looping forever, and says why.

**Managed.** A hosting control panel runs the game's lifecycle itself and leaves
`SnInstallRoot` empty. The supervisor then stays idle (it never launches or
kills the game) and only serves the query, RCON, HTTP and snapshot surfaces. The
control plane writes the process ids it started so its own stop path can find
them. If you are writing panel integration, this is the mode to target: leave
`SnInstallRoot` empty and own the process yourself, or set it and let BeaconServer
own it. Never both.

Two safety rails exist because getting this wrong is destructive:

- Config writes refuse a path that looks like a storefront install of the game,
  resolving junctions first, so a user directory accidentally pointed at their own
  copy of Subnautica 2 does not get its `Engine.ini` overwritten.
- The kill path only touches game processes running from under the configured
  install root, matched on a full path boundary. A normal session of the game on
  the same machine is not collateral.

## Wire formats

The protocol specs under `protocol/` are the source of truth for anything that
crosses a process boundary: the mod manifest, chat, the live map, the ModKit API
and the installer payload. `protocol/README.md` indexes them. If you change a
field, change the spec in the same commit.
