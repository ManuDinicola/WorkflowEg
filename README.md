# Flowd

Flowd is a BPMN 2.0 workflow engine (Camunda-like) written in C# 15 on .NET 11, with clean architecture and union types.
It deploys BPMN definitions, runs process instances durably on PostgreSQL, and coordinates external job workers, timers, messages and incidents through a REST API.

**Status:** milestone M0 (skeleton and tooling) is implemented on `feat/m0-skeleton`; see `PLAN.md` and `PROGRESS.md`. No BPMN execution yet (that is M1/M2).

## What works today
- Solution skeleton with enforced dependency rules (architecture tests) and C# 15 `union` / `closed class` smoke tests.
- `flowd migrate`: applies the PostgreSQL schema (forward-only DbUp scripts in `db/migrations`).
- `flowd serve [--auto-migrate]`: starts the HTTP host; refuses to start when migrations are pending.
- `GET /healthz` (liveness) and `GET /readyz` (database reachable and migrations current).

## Requirements
- .NET 11 SDK (RC for now; pinned in `global.json`).
- PostgreSQL 14-17.
- `make` (GNU Make) to run the quality gate. Docker is only needed for the integration tests (Testcontainers).

## Build and test
```
make lint               # format check, build with warnings as errors, architecture tests
make test-unit
make test-conformance
make test-integration   # Testcontainers, or set FLOWD_TEST_POSTGRES to an existing server
make cover              # engine core >= 90 %, overall >= 80 %
make ci                 # all of the above
```

## Run
```
set Flowd__ConnectionString=Host=localhost;Database=flowd;Username=flowd;Password=...
dotnet run --project src/Flowd.Host -- migrate
dotnet run --project src/Flowd.Host -- serve --urls http://localhost:8080
curl http://localhost:8080/readyz
```

## Deploying on a Windows server (no Kubernetes)
Flowd runs as a plain Windows Service next to PostgreSQL; no containers are required.
```
dotnet publish src/Flowd.Host -c Release -r win-x64 --self-contained -o C:\flowd
C:\flowd\flowd.exe migrate
sc.exe create Flowd binPath= "C:\flowd\flowd.exe serve" start= auto
```
Set `Flowd__ConnectionString` as a machine environment variable (or in `appsettings.json`). Point your monitoring or load balancer at `/healthz` and `/readyz`.
(The service wiring is in place; the install steps above will be verified and refined in M7.)

## Releases
Pushing a tag such as `v0.3.0` runs `.github/workflows/release.yml`: it runs `make ci`, publishes self-contained single-file packages (`flowd-<version>-win-x64.zip`, `flowd-<version>-linux-x64.tar.gz`, `SHA256SUMS.txt`) and creates a GitHub release with generated notes. Versions starting with `0.` or with a suffix such as `-rc.1` are marked pre-release.
```
git checkout main && git pull
git tag v0.3.0 && git push origin v0.3.0
```
To install a release on a Windows Server: extract the zip, then in an elevated PowerShell run
`.\install-service.ps1 -ConnectionString "Host=localhost;Database=flowd;Username=flowd;Password=..." -Urls "http://0.0.0.0:8080"`.
It applies the migrations and registers an auto-start service named `Flowd`. Run each engine node with a different `-NodeId`.

## Docs
- Spec index: `docs/spec/00-index.md` - plan: `PLAN.md` - decisions: `DECISIONS.md` - contributing: `docs/contributing.md`.
