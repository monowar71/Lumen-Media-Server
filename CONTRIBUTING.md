# Contributing to LumenMedia Server

Thank you for contributing. This document is the source of truth for how we accept changes.

## Code of Conduct

By participating, you agree to uphold the [Code of Conduct](CODE_OF_CONDUCT.md).

## Development model

- **Contract first:** API changes update OpenAPI (`openapi.json` / `/openapi/v1.json`) in the same PR.
- **Layering:** `Api → Application → Domain`; `Infrastructure` implements Application ports. Domain has no outward dependencies (enforced by architecture tests).
- **Testability:** new Application use-cases need unit tests; critical HTTP paths need integration tests.
- **Build in Docker:** use the .NET 10 SDK container — do not require a host SDK install.

```bash
docker run --rm -v "$PWD":/src -w /src \
  -v lumenmedia-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test LumenMedia.slnx
```

## Getting started

1. Fork and clone the repository.
2. Create a branch: `feat/<scope>-<short>` or `fix/<scope>-<short>`.
3. Make a minimal, focused change.
4. Run build + tests (Docker commands above).
5. Update docs / `openapi.json` when behavior or API changes.
6. Open a pull request using the template.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/):

- `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `chore:`, `ci:`, `perf:`, `build:`

English for identifiers, code comments, and commit messages.

## Pull requests

- One concern per PR; keep diffs reviewable.
- Describe *why*, link related issues, and list test evidence.
- Do not commit secrets, local DB files, or `*.local` config.
- Playback / transcoding changes should note manual verification steps.

## Reporting bugs / requesting features

Use the GitHub issue templates. Security issues go through [SECURITY.md](SECURITY.md) only.

## License

Contributions are accepted under the [GNU GPL v3](LICENSE). By submitting a PR, you agree your contribution is licensed under the same terms.
