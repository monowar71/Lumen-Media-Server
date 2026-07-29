# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.9] - 2026-07-29

### Added

- ThemerrDB theme songs: on metadata enrich, look up YouTube theme by TMDB id, download via yt-dlp to MP3 under `/config/metadata/{id}/theme.mp3`, expose `themeUrl` and `GET /api/v1/items/{id}/theme`.
- Docker image ships yt-dlp + Deno (YouTube JS challenge runtime) for theme extraction.

### Changed

- `/health` and server-info report version `0.1.9`.

## [0.1.8] - 2026-07-29

### Added

- HDR→SDR tonemap on Transcode (`forceHdrToSdr` / `HdrNotSupported`) via `zscale` + `tonemap`; admin setting `HdrToneMapMethod` (hable / mobius / reinhard / bt2390).
- Playback `audioLayout` (stereo / 2.1 / 5.1 / mono) with `availableAudioLayouts` in decision response; ffmpeg `-ac` / `-channel_layout`.
- ffprobe fills `MediaStream.Hdr` from color transfer / side data (HDR10, HDR10+, HLG, Dolby Vision).
- `/health` and server-info report version `0.1.8`.

## [0.1.7] - 2026-07-29

### Fixed

- Playback hang after quality change / seek: do not pause ffmpeg (`SIGSTOP`) until the player has requested a media segment; resume throttle on playlist GET.
- Web/Android clients mounting every subtitle `deliveryUrl` at once starved HLS behind long VTT extractions from large MKVs (browser ~6 connections/host).
- `auto` / `original` Transcode ignored `profile.maxResolution` and re-encoded full 4K @ 4000k when the reason was `ResolutionTooHigh`.

### Added

- Disk cache for converted WebVTT under `/config/subtitles/{sourceId}/{streamId}.vtt` (avoids re-scanning multi‑GB containers).
- `Paths:Subtitles` config; single-flight conversion per cache key.

### Changed

- `/health` and server-info report version `0.1.7`.

## [0.1.6] - 2026-07-28

### Changed

- Clearer production logs: quiet EF SQL / HttpClient noise; structured playback and ffmpeg start/stop/exit lines; job start/done with result; routine metadata enrich at Debug.
- `/health` and server-info report version `0.1.6`.

## [0.1.5] - 2026-07-28

### Changed

- VAAPI transcode now uses hardware decode (`-hwaccel vaapi`) and keeps frames on-GPU via `scale_vaapi=…:format=nv12` instead of software decode + `hwupload` (cuts CPU load on 4K HEVC).
- `/health` and server-info report version `0.1.5`.

## [0.1.4] - 2026-07-26

### Fixed

- Series scan matching uses `Title` or `OriginalTitle` so localized library cards do not spawn duplicate series.

## [0.1.3] - 2026-07-26

### Security

- Scope SignalR session events; enforce library ACL on progress/history; admin-only jobs; harden outbound URL fetches; refresh-token reuse revoke; Production CORS required.

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

[Unreleased]: https://github.com/monowar71/Lumen-Media-Server/compare/v0.1.9...HEAD
[0.1.9]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.9
[0.1.8]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.8
[0.1.7]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.7
[0.1.6]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.6
[0.1.5]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.5
[0.1.4]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.4
[0.1.3]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.3
[0.1.2]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.2
[0.1.1]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.1
[0.1.0]: https://github.com/monowar71/Lumen-Media-Server/releases/tag/v0.1.0
