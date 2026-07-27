# Beacon runtime

What actually runs on a Beacon server, in what order, and which piece owns what.
Read this before changing the supervisor, the Lua stack, or the identity model.

Subnautica 2 ships no dedicated server. Beacon runs the retail game headless as a
listen host and puts a supervisor, a query/RCON/HTTP surface and a set of in-game
mods around it. Everything below follows from that one fact.

## The two processes

**BeaconServer.exe** — a .NET 8 supervisor. It owns configuration, the admin
surfaces (Source A2S query, Source RCON, an HMAC-signed HTTP API), save snapshots
and restores, and — for a self-hosted install — the game process itself. It reads
`appsettings.json` from its own folder, not from the working directory it was
started in.

**Subnautica2-Win64-Shipping.exe** — the retail game, launched headless. UE4SS is
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
- The travel URL is the host trigger. `?listen` is required — a map URL without
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
a fixed timestep — that dilates game time instead of dropping frame rate), and a
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
outside, and RCON and HTTP only if you use them.

The HTTP API signs requests with `HMAC_SHA256(SHA256(RconPassword), "METHOD\npath\ntimestamp\nbody-sha256")`,
sent as `X-Beacon-Timestamp` and `X-Beacon-Signature`. There is a five minute
replay window and seen signatures are remembered inside it. There is no second
credential to manage: RCON and the HTTP API are the same trust tier.

## The in-game mod stack

One UE4SS mod is enabled in `mods.txt` — `BeaconServerRuntime`. It loads
everything else. Never enable the feature mods directly; load order matters and
the runtime mod is what guarantees it.

| Mod | What it does |
|---|---|
| `BeaconServerRuntime` | The single entry point. Loads the rest in order. |
| `BeaconModKit` | The shared Lua surface third-party mods write against. Stable API. |
| `BeaconLoader` | Brings `Beacon.dll` into the game process with `LoadLibrary` from Lua. UE4SS's own C++ loader expects an exact MSVC vtable layout and crashes the game without it. |
| `BeaconAuth` | The server password gate. Hooks post-login for incoming remote clients. Native enforcement exists in the plugin but is intentionally off — it crashes the host on spawn. |
| `BeaconRoster` | Publishes connected players to `roster.json` for the query and HTTP player lists. |
| `BeaconChat` | Chat command surface and in-game message delivery. |
| `BeaconNoPhantomHost` | Removes the listen host's own generated player from the player array, so gates and player lists see only real players. |
| `BeaconStoryGoalUnlock` | Unlocks the three chapter one lifepod story goals so the progression gate evaluates true on a host with no local player. |
| `BeaconLifepodFix` | Watches lifepod state during world boot and records what happened. Diagnostics, not enforcement. |
| `BeaconStarterBuilderUnlock` | Seeds per-player builder unlocks to match what a single-player start gives you. |
| `BeaconWeatherReplicationGuard` | Stops non-critical sky sequence playback from filling reliable RPC buffers on large worlds. |
| `BeaconWorldWarmup` | Keeps the spawn cells loaded so the first joiner does not arrive into a cold world. |

The roster has a redundancy worth knowing about: when nobody is connected the
game's tick rate collapses and the Lua roster loop can go ten minutes between
runs. BeaconServer tails the game log sub-second for join and leave lines and
that is the fast path. The Lua roster is the fallback. Do not retire either
without checking the other covers every case.

### `Beacon.dll`

A C++ plugin, cross-compiled with mingw-w64 and statically linked against libgcc
and libstdc++ — a dynamically linked build cannot find its runtime once it is
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
- A host that has no client identity file — any headless host — gets a
  deterministic identity derived from its configured instance id instead. This
  path has to stay: a blank host identity poisons the early lifepod flow before
  the phantom-host prune has a chance to run.
- The rejoin path accepts both the old plain-digits external ids and the newer
  composite form, so worlds created before character selection existed still load.

## Snapshots and restore

BeaconServer watches the save directory and takes snapshots on change. A restore
is not a live patch: the supervisor stops the game, swaps the save directory with
an atomic rename, re-arms the file watcher against the new directory — the old
watcher holds the renamed-away one — and relaunches. Anyone connected is
disconnected for the duration.

## Self-hosted versus managed

The supervisor has two modes and the difference is one setting.

**Self-hosted.** `SnInstallRoot` (or `SnExecutablePath`) points at the game.
BeaconServer stages UE4SS and `Beacon.dll` into the game folder, writes
`Engine.ini` and `steam_appid.txt`, launches the game, watches it, and relaunches
it if it exits. If the game exits fast enough times in a row the supervisor gives
up rather than looping forever, and says why.

**Managed.** A hosting control panel runs the game's lifecycle itself and leaves
`SnInstallRoot` empty. The supervisor then stays idle — it never launches or
kills the game — and only serves the query, RCON, HTTP and snapshot surfaces. The
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
