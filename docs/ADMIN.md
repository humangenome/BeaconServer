# Beacon admin guide

There are two ways to administer a Beacon server: the launcher's admin panel, and the RCON console.

## Launcher admin panel

If you set `RconPassword` on a server you control, the Beacon launcher exposes admin actions on that server's card:

| Action | What it does |
|---|---|
| **Take snapshot** | Saves the current world state as a named snapshot on the server. Use before risky merges, mod changes, or wipes. |
| **Browse snapshots** | Lists every snapshot the server has, with timestamps and sizes. |
| **Restore snapshot** | Rolls the world back to a snapshot. The server pauses, swaps the save file atomically, and resumes. Connected players are dropped and can reconnect. |
| **Start / Stop server** | Brings the Subnautica 2 game process up or down. Beacon itself stays running. |

Snapshots are also taken on a server-controlled schedule automatically, so you usually don't need to manually snapshot before normal play.

## RCON commands

Beacon listens on a [Source-protocol RCON](https://developer.valvesoftware.com/wiki/Source_RCON_Protocol) port (gameplay port + 3). Use any standard RCON client (`mcrcon`, BattleMetrics, the launcher's Console tab, etc.).

### Connecting

- **Host:** your server's IP
- **Port:** `<gameplay port> + 3` (e.g. if gameplay is `7777`, RCON is `7780`)
- **Password:** the `RconPassword` you set in `appsettings.json`

### Commands

| Command | Output | Purpose |
|---|---|---|
| `status` | `instance=<id> plugin=connected pid=<n> version=<v> players=<n>` | Confirms the in-game plugin is connected, reports the player count. |
| `players` | Current player count | Quick "is anyone on" check. |
| `ping` | `pong` | RCON heartbeat / connectivity test. |
| `save snapshot` | `snapshot ok: <id> (<bytes>, sha=<short>)` | Force a save snapshot right now. |
| `save list` | One snapshot per line (id, size, age, sha) | List the 20 most recent snapshots. |

### Examples

```
> status
instance=adminserver plugin=connected pid=8588 version=0.3.42 players=2

> save snapshot
snapshot ok: 2026-05-17T03-12-04Z (4317829 bytes, sha=a91e3f04b2c97d18)

> save list
2026-05-17T03-12-04Z  4317829B  age=42s  sha=a91e3f04b2c97d18
2026-05-17T02-44-19Z  4317021B  age=1665s sha=7c81f9302b03e9d4
...
```

## HTTP admin API (for power users)

The launcher's admin actions go over an HMAC-signed HTTP API on `<gameplay port> + 4`. If you want to script the same actions from your own tools, the API is documented inline in `src/server/BeaconServer/Services/BeaconHttpService.cs`. We'll publish a standalone reference once the surface stabilizes.

The short version: every request signs `HMAC_SHA256(SHA256(RconPassword), "{METHOD}\n{path}\n{timestamp}\n{body_sha256}")` and sends it as `X-Beacon-Signature` with `X-Beacon-Timestamp`. Replay window is 5 minutes.
