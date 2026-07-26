# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.2] - 2026-07-26

### Added

- Audio and subtitle track titles from container metadata (`tags.title`) — e.g. dubbing studios like LostFilm / MovieDalen — exposed on playback decision options and item streams.
- Library re-scan backfills missing stream titles and disposition flags without re-importing files.

### Changed

- `/health` and server-info report version `0.1.2`.

## [0.1.1] - 2026-07-26

### Fixed

- Android / native players no longer stop every 15 minutes: HLS and DirectPlay under `/api/v1/stream/{sessionId}/…` use the session id as a capability URL, so playback continues after the short-lived access JWT expires. DirectPlay `streamUrl` is now `/api/v1/stream/{sessionId}/source`.
- Faster HLS restart after seek.
- More stable AC3 remux; broader quality ladder.

### Changed

- CI Docker images are multi-arch (`linux/amd64`, `linux/arm64`), built natively on `ubuntu-latest` + `ubuntu-24.04-arm` (no QEMU).

## [0.1.0] - 2026-07-20

First tagged release. Images are published to Docker Hub (`monowar71/lumenmedia-server`); GitHub Releases carry notes only.

### Added

- Open-source repository scaffolding (license, contributing guide, security policy, CI, issue/PR templates).
- Domain / Application / Infrastructure / Api layers, SQLite, OpenAPI, Docker image with ffmpeg.
- Artwork candidates API and item artwork service.
- Auto-scan libraries, unmatched Plex history handling, and media file delete.
- Watch history API, Plex history import, and series duplicate merge.
- Episode metadata, cast credits, and trailers from TMDB.
- Library metadata refresh, VAAPI encode path, and improved TMDB scoring.
- Metadata keys, TVDB provider, and manual match/edit flows.
- Metadata language settings wired to TMDB with re-enrich.
- Series next-up resolution for play CTAs.
- CI publishes `linux/amd64` images to Docker Hub on `main` (`nightly`) and `v*` tags (`latest`, semver).

### Changed

- Renamed product branding from FreePlex to LumenMedia.
- Distribution channel: Docker Hub instead of GitHub Release assets.

### Fixed

- Full Plex history import and resume progress.
- Case-sensitive Guid binding when parsing Plex JSON.
- Force transcode for manual non-original quality.
- Background jobs, scanning, and auth hardening.
- Shared-folder scan classification and artwork insert persistence.

[Unreleased]: https://github.com/monowar71/Lumen-Media-Server/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.2
[0.1.1]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.1
[0.1.0]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.0
