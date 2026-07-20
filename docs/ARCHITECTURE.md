# Architecture overview

LumenMedia Server is the sole business-logic host for the LumenMedia media stack. Clients (iOS/tvOS, Android/TV, Web) speak HTTP(S), SignalR, and consume HLS.

## Principles

- **Server owns domain logic** — clients stay thin (UI + playback).
- **Hexagonal architecture** — DB, ffmpeg, metadata providers, and filesystem sit behind ports.
- **Contract-first** — OpenAPI is the source of truth; client SDKs are generated from it.
- **Async by default** — scan, import, transcode, and metadata run as background jobs.

## Solution layout

```
src/
  LumenMedia.Domain          # entities & invariants; no external deps
  LumenMedia.Application     # ports + use-cases + DTOs
  LumenMedia.Infrastructure  # EF Core/SQLite, ffmpeg, TMDB/TVDB, watchers, workers
  LumenMedia.Api             # ASP.NET Core, JWT, SignalR, OpenAPI, HLS
tests/
  LumenMedia.Domain.Tests
  LumenMedia.Application.Tests
  LumenMedia.Api.IntegrationTests
```

Dependency direction:

```
Api → Application → Domain
Infrastructure → Application (implements ports)
```

## Runtime shape

```
Clients ──REST/SignalR/HLS──▶ LumenMedia.Api
                                  │
                                  ▼
                            Application use-cases
                                  │
                    ┌─────────────┼─────────────┐
                    ▼             ▼             ▼
                 SQLite        ffmpeg      TMDB / TVDB
                 (/config)   (HW/VAAPI)    (metadata)
```

## Critical path

Playback and transcoding are the critical path. Changes there require integration or manual playback verification. See [CONTRIBUTING.md](../CONTRIBUTING.md) and [SECURITY.md](../SECURITY.md).
