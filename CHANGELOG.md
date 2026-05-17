# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
versioning is [SemVer](https://semver.org/).

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
