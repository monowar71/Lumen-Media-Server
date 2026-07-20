## Summary

<!-- Why this change exists. Link related issues. -->

-

## Changes

-

## Test plan

- [ ] `dotnet build` / `dotnet test` via Docker SDK image (see CONTRIBUTING.md)
- [ ] Updated `openapi.json` if the HTTP contract changed
- [ ] Updated docs / CHANGELOG if user-visible behavior changed
- [ ] Manual playback check (only if player / transcoding touched)

## Checklist

- [ ] Layering preserved (`Api → Application → Domain`; Infrastructure behind ports)
- [ ] No secrets committed
- [ ] Conventional Commit title (`feat:`, `fix:`, …)
