# Security Policy

## Supported versions

| Version | Supported |
| --- | --- |
| `main` (pre-1.0 development) | Yes |
| Tagged releases (when published) | Latest minor only |

## Reporting a vulnerability

**Do not open a public issue for security vulnerabilities.**

Please report security issues privately using one of:

1. [GitHub Security Advisories](https://github.com/monowar71/Lumen-Media-Server/security/advisories/new) (preferred)
2. A private message to the repository owner via GitHub

Include:

- Description of the issue and impact
- Steps to reproduce (PoC if available)
- Affected commit / version / deployment mode (Docker, reverse proxy, LAN-only)

We aim to acknowledge reports within **72 hours** and to provide a remediation plan or fix timeline within **14 days** for confirmed issues affecting authentication, authorization, path traversal, media parsing (ffmpeg), or secret handling.

## Security expectations for this project

LumenMedia Server is a self-hosted media backend. Operators should:

- Set a strong `JWT__SECRET` (min 32 bytes) in production
- Keep the API behind a reverse proxy with TLS when exposed beyond a trusted LAN
- Restrict filesystem mounts (`/media`, `/downloads`, `/config`) to intended paths only
- Treat TMDB/TVDB API keys as secrets (env / secret store only)
- Keep the container non-root (`app` user) and avoid granting unnecessary device access

## Out of scope (typical)

- Denial of service against a LAN-only home deployment without authentication bypass
- Issues that require an already-compromised host or malicious media files without a clear privilege-escalation path beyond the container sandbox (still welcome as hardening reports)
