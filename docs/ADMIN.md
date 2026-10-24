# Beacon admin guide

There are three ways to administer a Beacon server: the launcher's admin tools, the RCON console, and the HMAC-signed HTTP API.

## Launcher admin tools

If you save the server's admin password (the `RconPassword` from `appsettings.json`) in the Beacon launcher, two admin surfaces unlock for that server:

| Surface | What it does |
|---|---|
| **World** button → *World backups* dialog | Lists the server's snapshots with their local times and sizes, and downloads a snapshot zip. On a server where BeaconServer 0.3.132 or newer starts the game itself, it also restores a selected snapshot, imports a save (a zip of a Subnautica 2 SaveGames folder, or a previously downloaded snapshot) and migrates the newest snapshot from another of your servers, each after a confirmation. Before 0.3.132 the dialog showed only the password row. |
| **Console** tab | A Source RCON console with history and command autocomplete. Use `save snapshot` here to take a snapshot on demand. |

Restore and import stop the Subnautica 2 process, take a pre-restore snapshot (so the operation is reversible), swap the save directory atomically, and relaunch the game. Connected players are dropped and can reconnect. An imported world can be in any `savegame_N.sav` slot: the newest one becomes `savegame_0.sav`, the slot the server loads.

Restore and import need BeaconServer to start the game itself (`SnInstallRoot` or `SnExecutablePath` set), because only then does the world come back up after the swap. A server whose game is started by a hosting panel answers both with HTTP 409 and reports `"world_restore": false` in `/api/v1/health`; use the host's own backups there. Downloading snapshots works on both.

Snapshots are also taken automatically on every game auto-save (Subnautica 2 auto-saves about once a minute; Beacon debounces to at most one snapshot per ~45 seconds) while `SnapshotsEnabled` is on, and are rotated out after 30 days. You usually don't need to snapshot manually before normal play.

## RCON commands

Beacon listens on a [Source-protocol RCON](https://developer.valvesoftware.com/wiki/Source_RCON_Protocol) port: the `RconPort` setting, default `27018`. Managed hosts and the launcher follow the convention `RconPort = gameplay port + 3`. Use any standard RCON client (`mcrcon`, BattleMetrics, the launcher's Console tab, etc.).

### Connecting

- **Host:** your server's IP
- **Port:** `RconPort` from `appsettings.json` (default `27018`; by convention `<gameplay port> + 3`, e.g. gameplay `27015` → RCON `27018`)
- **Password:** the `RconPassword` you set in `appsettings.json`; RCON is disabled while it is empty

### Commands

| Command | Output | Purpose |
|---|---|---|
| `help` | The built-in command list | Quick reference. |
| `status` | `instance=<id> plugin=connected pid=<n> version=<v> players=<n>` (or `plugin=disconnected`) | Confirms the in-game plugin is connected, reports the player count. |
| `players` | `<n> player(s) online`, or `no players online` | Quick "is anyone on" check. |
| `ping` | `pong` | RCON heartbeat / connectivity test. |
| `save snapshot` | `snapshot ok: <id> (<bytes> bytes, sha=<short>)` | Force a save snapshot right now. |
| `save list` | One snapshot per line (id, size, age, sha) | List the 20 most recent snapshots. |
| `say <message>` | `chat ok (system): <message>` | Broadcast a system message to every player's chat overlay. |
| `announce <message>` | `chat ok (admin): <message>` | Broadcast an admin announcement. |
| `motd [message]` | `motd: <current>` / `motd updated` | Read or set the message of the day. |
| `/<command> [args]` | The mod's reply | Run a slash command registered by a server mod (the leading `/` is optional). RCON callers are always treated as admin. |

There is **no restore command over RCON**. Restore a snapshot from the launcher's World backups dialog or with `POST /api/v1/snapshots/<id>/restore` on the HTTP API.

### Examples

```
> status
instance=adminserver plugin=connected pid=8588 version=0.3.101 players=2

> save snapshot
snapshot ok: snap-20260517T031204Z-7c9e2ab04d113e (4317829 bytes, sha=a91e3f04b2c97d18)

> save list
snap-20260517T031204Z-7c9e2ab04d113e  4317829B  age=42s  sha=a91e3f04b2c97d18
snap-20260517T024419Z-1f2a3b4c5d6e0f  4317021B  age=1665s  sha=7c81f9302b03e9d4
...
```

## HTTP admin API (for power users)

The launcher's snapshot, restore, and import actions go over an HMAC-signed HTTP API on the `HttpPort` setting (default `27019`; by convention `<gameplay port> + 4`). The API is disabled when `HttpPort` is `0` or `RconPassword` is empty. If you want to script the same actions from your own tools, the API is documented inline in `src/server/BeaconServer/Services/BeaconHttpService.cs`.

Every signed request computes `HMAC_SHA256(SHA256(RconPassword), "{METHOD}\n{path}\n{timestamp}\n{body_sha256}")` and sends it as `X-Beacon-Signature` with `X-Beacon-Timestamp` (unix seconds). The replay window is 5 minutes, and an accepted signature cannot be replayed inside it. A request with a body sends `Content-Length` and the body's SHA-256 as `X-Beacon-Content-Sha256`; for a body over 64 KB that header is required, and the server checks the signature before it reads the body.

Routes, all under `/api/v1` unless noted:

**Public (no signature):**

- `GET /health`: instance, version, ports, player count, plugin status, and `chat_gate`: `"ok"`, or `"no_connections_seen"` when players have been on for 10 minutes but no game connection could be read from the game log (player chat and the live map then refuse everyone; `chat_gate_since_unix` says since when, otherwise `null`). Stopgap while it reads `no_connections_seen`: `Beacon:Chat:PublicRead` and `Beacon:Map:Public`.
- `GET /players`: connected players as JSON
- `GET /manifest`: the server's mod manifest
- `GET /chat/motd`: the MOTD
- `GET /map/` (top-level path): the live web map page

**Players on this server (matched by the address their game connected from), the host machine, or signed:**

- `GET /chat/recent`: chat feed (anyone when `Beacon:Chat:PublicRead` is `true`)
- `POST /chat/player`: player chat ingest (rate-limited; the line carries the player's in-game name)
- `POST /map/position`: the player's own position for the live map
- `GET /map/state`: live map state (also anyone when `Beacon:Map:Public` is `true`, or a request carrying `Beacon:Map:AccessKey` as `X-Beacon-Map-Key`)

**Signed:**

- `GET /info`: instance metadata
- `GET /snapshots`: list snapshots
- `GET /snapshots/{id}/download`: download a snapshot zip
- `POST /snapshots`: upload a zip and store it as a snapshot
- `POST /snapshots/{id}/restore`: restore a stored snapshot
- `POST /snapshots/import-restore`: upload a save zip and restore it in one shot
- `POST /chat/say`, `POST /chat/motd`: broadcast chat / set MOTD
