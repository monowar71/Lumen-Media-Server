# LumenMedia Server

[![CI](https://github.com/monowar71/Lumen-Media-Server/actions/workflows/ci.yml/badge.svg)](https://github.com/monowar71/Lumen-Media-Server/actions/workflows/ci.yml)
[![License: GPL-3.0](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](global.json)

**Self-hosted media server backend** for movies and TV shows — libraries, metadata, watch progress, and on-the-fly HLS transcoding. Part of the [LumenMedia](https://github.com/monowar71) stack.

> Clients talk HTTP(S) + SignalR and play HLS. Business logic lives only on the server.

## Features

- **Libraries** — scan movies / series from folders; auto-scan with debounce + reconcile
- **Metadata** — TMDB / TVDB enrichment, artwork, cast, trailers, manual match & edit
- **Playback** — direct play or ffmpeg HLS (HW accel, VAAPI); ABR-friendly segments
- **Progress & history** — resume points, watch history, optional Plex history import
- **Import pipeline** — watch a downloads folder (hardlink / copy strategies)
- **Contract-first API** — OpenAPI (`openapi.json`), JWT auth, SignalR realtime jobs
- **Ops-friendly** — single Docker image, embedded SQLite under `/config`, non-root `app` user

## Quick start

### Production image

CI pushes to Docker Hub on `main` (`nightly`, `sha-…`) and on `v*` tags (`latest`, semver):

```bash
docker pull monowar71/lumenmedia-server:nightly
# or: docker pull monowar71/lumenmedia-server:latest

docker run --rm -p 8096:8096 \
  -e JWT__SECRET="$(openssl rand -base64 48)" \
  -v lumenmedia-config:/config \
  -v /path/to/media:/media \
  -v /path/to/downloads:/downloads \
  monowar71/lumenmedia-server:nightly
```

Build locally:

```bash
docker build -t lumenmedia-server .
```

Optional Intel VAAPI:

```bash
docker run --rm -p 8096:8096 \
  --device /dev/dri --group-add video \
  -e JWT__SECRET="$(openssl rand -base64 48)" \
  -v lumenmedia-config:/config \
  -v /path/to/media:/media \
  monowar71/lumenmedia-server:nightly
```


Then:

1. `GET /api/v1/server/info` → `setupCompleted: false`
2. `POST /api/v1/setup` with `{ "username", "password" }`
3. `POST /api/v1/auth/login` → use `Authorization: Bearer <accessToken>`
4. OpenAPI: `GET /openapi/v1.json` · Health: `GET /health`

### Development (Docker SDK — no host .NET required)

```bash
git clone https://github.com/monowar71/Lumen-Media-Server.git
cd Lumen-Media-Server

docker run --rm -v "$PWD":/src -w /src \
  -v lumenmedia-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test LumenMedia.slnx
```

Run the API:

```bash
docker run --rm -p 5080:5080 -v "$PWD":/src -w /src \
  -v lumenmedia-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project src/LumenMedia.Api --urls http://0.0.0.0:5080
```

## Architecture

Hexagonal / clean layering:

| Project | Role |
| --- | --- |
| `LumenMedia.Domain` | Entities & invariants — no external dependencies |
| `LumenMedia.Application` | Ports + use-cases + DTOs |
| `LumenMedia.Infrastructure` | EF Core + SQLite, ffmpeg, TMDB/TVDB, watchers, workers |
| `LumenMedia.Api` | ASP.NET Core, JWT, SignalR, OpenAPI, HLS |

```
Api → Application → Domain
Infrastructure → Application  (implements ports)
```

More detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Configuration

| Key | Environment | Default / notes |
| --- | --- | --- |
| `Jwt:Secret` | `JWT__SECRET` | **Required in Production** (≥ 32 bytes) |
| `LumenMedia:Database:ConnectionString` | `LUMENMEDIA__Database__ConnectionString` | `Data Source=/config/lumenmedia.db` |
| `LumenMedia:Paths:Config` | `LUMENMEDIA__Paths__Config` | `/config` |
| `LumenMedia:Paths:Transcodes` | `LUMENMEDIA__Paths__Transcodes` | `/config/transcodes` |
| `LumenMedia:Transcoding:*` | `LUMENMEDIA__Transcoding__*` | see `appsettings.json` |
| `Cors:AllowedOrigins` | `CORS__ALLOWEDORIGINS__0` … | empty = any origin (LAN); set explicitly for internet |
| TMDB / TVDB keys | env / secret store | never commit |

Secrets must not appear in the repository or logs. Auth endpoints are rate-limited (10 req/min/IP).

Bind mounts for `/config`, `/media`, and `/downloads` must be writable by UID **1654** (`app`).

## Repository layout

```
├── src/                 # Domain, Application, Infrastructure, Api
├── tests/               # Unit + integration tests
├── docs/                # Architecture notes
├── openapi.json         # Exported HTTP contract (committed)
├── Dockerfile           # Multi-stage: sdk → aspnet + ffmpeg
├── LumenMedia.slnx      # Solution
└── .github/             # CI, issue/PR templates, Dependabot
```

## Related clients

| Repo | Role |
| --- | --- |
| [Lumen-Media-iOS](https://github.com/monowar71/Lumen-Media-iOS) | iOS / iPad (SwiftUI) |
| [Lumen-Media-Android](https://github.com/monowar71/Lumen-Media-Android) | Android / Android TV |
| [Lumen-Media-Web](https://github.com/monowar71/Lumen-Media-Web) | Web (React + Vite) |

## Contributing

Please read [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md).

- Bugs & features: issue templates under `.github/ISSUE_TEMPLATE/`
- Security: [SECURITY.md](SECURITY.md) (private advisories only)
- Changelog: [CHANGELOG.md](CHANGELOG.md)

## License

[GNU General Public License v3.0](LICENSE) — see also [SUPPORT.md](SUPPORT.md).

Copyright © 2026 Alexander Goncharow and contributors.
