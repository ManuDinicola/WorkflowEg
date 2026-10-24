# Flowd

Flowd is a BPMN 2.0 workflow engine (Camunda-like) written in C# 15 on .NET 11, with clean architecture and union types.
It deploys BPMN definitions, runs process instances durably on PostgreSQL, and coordinates external job workers, timers, messages and incidents through a REST API.

**Status:** milestones M0-M3 are implemented (skeleton, deployment, core execution, structure and control); see `PLAN.md` and `PROGRESS.md`. Next: timers and messages (M4).

## What works today
- Solution skeleton with enforced dependency rules (architecture tests) and C# 15 `union` / `closed class` smoke tests.
- `flowd migrate`: applies the PostgreSQL schema (forward-only DbUp scripts in `db/migrations`).
- `flowd serve [--auto-migrate]`: starts the HTTP host; refuses to start when migrations are pending.
- `GET /healthz` (liveness) and `GET /readyz` (database reachable and migrations current).
- `flowd validate <file.bpmn>`: checks a BPMN file offline and lists every problem (exit code 1 when invalid).
- `POST /v1/deployments` (multipart `.bpmn` files, XXE-safe, all-or-nothing, versioned by checksum), `GET /v1/process-definitions`, `GET /v1/process-definitions/{key}` and `/xml`.
- `POST /v1/process-instances`, `GET /v1/process-instances[/{key}]`, `.../cancel`, `PUT .../variables`, `GET .../history`; `POST /v1/jobs/activate` (long poll) and `/v1/jobs/{key}/complete|fail|error`, `PATCH /v1/jobs/{key}`; `GET /v1/incidents`, `POST /v1/incidents/{key}/resolve`.
- flowd-FEEL expressions (conditions, input/output mappings, dynamic job types and retries).

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

## Deploying a process
```
curl -F "resources=@testdata/bpmn/order-simple.bpmn" http://localhost:8080/v1/deployments
curl http://localhost:8080/v1/process-definitions
```
Supported BPMN so far: none start event, none/terminate end event, service/send/business-rule tasks (all worker-based, `flowd:taskDefinition`), script task (expression only), manual task, embedded sub-process, exclusive, parallel and inclusive gateways. Anything else is rejected with its element id (more elements arrive in M4-M6).

## Running a process
```
curl -X POST http://localhost:8080/v1/process-instances -H "Content-Type: application/json" -d '{"bpmnProcessId":"order-process","variables":{"order":{"total":50}}}'
curl -X POST http://localhost:8080/v1/jobs/activate -H "Content-Type: application/json" -d '{"type":"charge-card","worker":"w1","timeoutMs":30000,"maxJobs":10,"requestTimeoutMs":10000}'
curl -X POST http://localhost:8080/v1/jobs/<jobKey>/complete -H "Content-Type: application/json" -d '{"variables":{"receiptId":"R-1"}}'
```
Jobs are delivered at least once: workers must be idempotent. A job that is not completed before its deadline is handed to another worker.
