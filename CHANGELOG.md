# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
versioning is [SemVer](https://semver.org/).

## [0.3.127] - 2026-08-01

### Fixed

- Diving deep no longer gets a player teleported back to the escape pod. The
  deep-start recovery treated anyone below 400 m as stranded at the start of the
  game, so on a world that had been played for a while it kept pulling divers
  back to the surface. It now only lifts a player who genuinely spawned at the
  deep start and could not get out, and it does that once.
- Riding a creature no longer confuses that recovery. The mount was being treated
  as a player and moved along with the rider.
- The server no longer freezes while the recovery runs. A pass with nobody to
  rescue does no work at all, and the work it does do is spread over several
  frames instead of blocking one.

### Added

- The server records whether save-handle protection resolved on the current game
  build, so a host can check it per server.

## [0.3.126] - 2026-07-30

### Changed

- Rebuild of the v0.3.125 server bundle with no functional change. The binaries no
  longer carry the build machine's directory layout or a debug link back to where they
  were compiled, and the build now fails if either comes back.
- The bundled server mod's comments no longer name an internal hosting-side function.
  The mod ships as readable source, so its comments are part of what this release
  publishes; behaviour is unchanged.

## [0.3.125] - 2026-07-27

### Fixed

- Subnautica 2 no longer closes itself with "The game was not started via the
  platform launcher" on hosts that have a Steam client available. The server now
  writes `steam_appid.txt` next to the game executable and passes the same
  application id to the game through the environment, which is what Steam itself
  supplies when it starts a game.
- When the game does close itself over that check, the server says so in one line
  and stops relaunching instead of boot-looping.
- Haul chassis and Cicada debris deconstruct now resolve to their recipes, so both
  tadpole chassis appear in the blueprint list and the Vehicle Fabricator.

### Changed

- Source synced to Beacon 0.3.125. The 0.3.122–0.3.124 tags on this repo all
  pointed at the 0.3.122 source snapshot; this release closes that gap.

## [0.3.124] - 2026-07-27

### Fixed

- Scout Ray chassis progression now follows its scanned recipe correctly on hosted
  worlds.

## [0.3.123] - 2026-07-22

### Changed

- No server-side changes. Released alongside launcher update-handling fixes.

## [0.3.122] - 2026-07-08

### Synced to current source

- Source caught up to Beacon 0.3.122 (this repo was previously synced at
  0.3.102).
- Config now loads from next to `BeaconServer.exe` regardless of the working
  directory the server is started from, so an edited `appsettings.json` is
  always honored (fixes #9).
- Server performance: the generated `Engine.ini` caps the headless host at
  `t.MaxFPS=30` (real frame deltas — no fixed timestep, no time dilation) and
  raises net connection timeouts to 300s for slow-loading clients.
- The UE4SS server settings patcher now re-asserts the keys the server stack
  depends on (`bUseUObjectArrayCache`, `HookProcessInternal`,
  `HookProcessLocalScriptFunction`, `HookEngineTick`), healing a client-profile
  settings file back to server semantics.
- Generated listen-host player names (`server-<hex>`, `ns<digits>-<hex>`,
  `WIN-*`) are detected more precisely so they stay out of player lists without
  ever filtering real player names.
- Command-queue file writes use unique temp names with retry, fixing rare
  file-in-use races on busy hosts.

### Fixed

- `dotnet restore` no longer fails with security-advisory errors
  (NU1902/NU1903): MessagePack updated to 3.1.7. This had broken the automated
  release builds after 0.3.120.

## [0.3.102] - 2026-06-10

### Synced to current source

- Source caught up to Beacon 0.3.102 (this repo had been frozen at the 0.3.42
  snapshot). Adds in-game chat, the mod manifest + auto-install flow, the live
  web map (`/map/` plus the position and state endpoints), the `say` /
  `announce` / `motd` RCON commands and mod slash-command dispatch, the A2S
  player list, snapshot + restore over the HTTP API, and `BeaconAuthPassword`
  join auth.
- Build and 86 tests pass on this source.

### Self-host runtime note

- The release zip is now self-contained — it bundles the in-game runtime (the
  `ue4ss\` mods plus the native `Beacon.dll`) with the MIT-licensed supervisor,
  so a downloaded server is playable as-is. Building from this source yields the
  supervisor only. This supersedes the 0.3.42 note that pointed self-hosters at
  the umbrella release page.

## [0.3.42] - 2026-05-20

### Current production source

- Synced BeaconServer source to the server binary shipped in Beacon 0.3.42.
- Added current save-slot normalization, roster file watching, password-gated
  heartbeat enforcement, configurable player count, and snapshot toggle code.
- Clarified that the server-only zip contains MIT BeaconServer binaries only;
  playable self-host installs should use the full Beacon bundle from the
  umbrella Beacon release page.

## [0.2.3] - 2026-05-17

### Initial open-source release

BeaconServer is now developed in this public repository under MIT. Prior version
history of the broader Beacon project lived in a private repo; this changelog
starts fresh from the first public release.

### Current state (v0.2.3)

- Subnautica 2 process supervisor with crash-recovery and graceful shutdown
- Named-pipe IPC channel to the in-game UE4SS plugin (length-prefixed +
  HMAC-SHA256 authenticated frames)
- Save snapshot orchestrator with on-disk atomic restore (rename-based; rollback
  on extract failure; zip validation rejects obvious wrong-folder uploads)
- Source A2S query responder (Goldsource-compatible) on `gameplay port + 2`
- Source RCON listener on `gameplay port + 3`
- HMAC-signed HTTP admin API on `gameplay port + 4`:
  - `GET /api/v1/health` (public)
  - `GET /api/v1/players` (public — Source A2S info as JSON)
  - `GET /api/v1/info` (HMAC-auth)
  - `GET /api/v1/snapshots`, `GET /api/v1/snapshots/{id}/download` (HMAC-auth)
  - `POST /api/v1/snapshots`, `POST /api/v1/snapshots/{id}/restore`,
    `POST /api/v1/snapshots/import-restore` (HMAC-auth)
- 5-minute replay window with anti-replay tracking
- Streaming body upload to temp file with inline SHA256 (large uploads don't
  triple-allocate)
- HTTP listener self-recovers after transient failures with 5-second backoff
- 27 tests across `Beacon.Protocol.Tests`, `BeaconServer.Tests`, and
  `Beacon.Integration.Tests`
