# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
versioning is [SemVer](https://semver.org/).

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
