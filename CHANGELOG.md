# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
once tagged releases begin.

## [Unreleased]

### Added

- Open-source repository scaffolding (license, contributing guide, security policy, CI, issue/PR templates).
- Artwork candidates API and item artwork service.
- Auto-scan libraries, unmatched Plex history handling, and media file delete.
- Watch history API, Plex history import, and series duplicate merge.
- Episode metadata, cast credits, and trailers from TMDB.
- Library metadata refresh, VAAPI encode path, and improved TMDB scoring.
- Metadata keys, TVDB provider, and manual match/edit flows.
- Metadata language settings wired to TMDB with re-enrich.

### Changed

- Renamed product branding from FreePlex to LumenMedia.

### Fixed

- Full Plex history import and resume progress.
- Case-sensitive Guid binding when parsing Plex JSON.
- Force transcode for manual non-original quality.
- Background jobs, scanning, and auth hardening.
- Shared-folder scan classification and artwork insert persistence.

## [0.1.0] - 2026-07-19

### Added

- Initial LumenMedia Server import: Domain / Application / Infrastructure / Api layers, SQLite, OpenAPI, Docker image with ffmpeg.
