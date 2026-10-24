# Contributing

## Workflow
- Never commit to `main`. One branch per milestone (`feat/m<N>-<slug>`); see `CLAUDE.md` and `docs/spec/01-rules.md` (GIT-001..009).
- Before every push run `make ci` (lint, unit, conformance, integration, coverage). Integration tests need a running Docker daemon.
- Open a pull request against `main` with a Test report (gate output and the `T-…` IDs that pass).

## Tooling
- .NET 11 SDK pinned in `global.json` (RC until GA), `make` (GNU Make), Docker.
- Tests use `Microsoft.Testing.Platform`: `dotnet test --project tests/<Project>`.

## Branch protection (CI-004)
Configure on GitHub (Settings → Branches → rule for `main`):
- Require a pull request with 1 approving review before merging.
- Require status check `build-test (14)` and `build-test (17)` to pass.
- Disallow force pushes and deletions.
