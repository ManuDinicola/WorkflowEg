# Operations runbook

## Install on a Windows server (no Kubernetes)
1. Install PostgreSQL 14-17 and create a database and user.
2. Download the release zip, extract it, then in an elevated PowerShell:
   `.\install-service.ps1 -ConnectionString "Host=localhost;Database=flowd;Username=flowd;Password=..." -Urls "http://0.0.0.0:8080"`
   This migrates the database and registers the `Flowd` auto-start service. Use a distinct `-NodeId` per node.
3. Create the first API key with `flowd apikey create` `--name ops --roles admin [--tenant acme]`; roles are admin, operator, developer, worker, reader. The key is shown once.
4. Put a TLS-terminating reverse proxy (IIS ARR, nginx, Caddy) in front. Flowd speaks plain HTTP.

The install script has not been tested on a real Windows Server yet; try it on a staging machine first.

## Human tasks
User tasks carry `dueDate`, `followUpDate` and `priority` (0-100, default 50) in `<flowd:taskSchedule .../>`. `GET /v1/user-tasks` filters by `assignee`, `candidateGroup`, `candidateUser`, `state`, `processInstanceKey`, `minPriority` and `dueBefore`, with paging.

## Configuration (environment variables or `appsettings.json`, section `Flowd`)
| Setting | Meaning |
|---|---|
| `Flowd__ConnectionString` | PostgreSQL connection string (required) |
| `Flowd__Auth__Insecure` | `true` disables authentication. Development only |
| `Flowd__Auth__...` | JWT signing key plus issuer, or an OIDC authority |
| `Flowd__WebhookUrl`, `Flowd__WebhookSecret`, `Flowd__OutboxEnabled` | signed lifecycle webhooks |
| `Flowd__Retention__InstanceDays`, `HistoryDays`, `OutboxDays`, `BatchSize`, `IntervalSeconds` | janitor retention (defaults 30/30/7/500/60) |

## Health and monitoring
- `/healthz` liveness, `/readyz` database reachable and migrations current.
- `/metrics` Prometheus text. Alert on rising incident count, job activation latency and 5xx rate.
- Logs are JSON on stdout; variable values are never logged at Info.

## Backup and recovery
All state is in PostgreSQL: use `pg_dump` or continuous archiving. After a restore, start the service; jobs and timers whose lease expired are picked up automatically. A crash while running is safe (see the chaos test `T-CHAOS-01`).

## Upgrades
Migrations are forward-only. Stop the old version, run `flowd migrate` (or start with `--auto-migrate`), start the new version. `flowd serve` refuses to start while migrations are pending.

## Troubleshooting
- Instance stuck: `GET /v1/incidents`, fix the cause (variable, worker), then `POST /v1/incidents/{key}/resolve`.
- 429 responses: the rate limit was reached; clients should back off.
- 422 `IDEMPOTENCY_KEY_REUSED`: the same `Idempotency-Key` was sent with a different body.

## Backup and restore
All state is in PostgreSQL. `scripts/backup.ps1` wraps `pg_dump` / `pg_restore` (custom format, compressed):
```
.\backup.ps1 -Action Backup  -ConnectionString "Host=localhost;Database=flowd;Username=flowd;Password=..." -File D:\backups\flowd.dump
.\backup.ps1 -Action Restore -ConnectionString "Host=localhost;Database=flowd_restored;Username=flowd;Password=..." -File D:\backups\flowd.dump
```
Pass `-PgBin "C:\Program Files\PostgreSQL\16\bin"` when the PostgreSQL tools are not on the PATH. Restore into an empty database, then start Flowd against it. Test your restore regularly.

## Deleting data on demand
`DELETE /v1/process-instances/{key}` removes a finished (completed or terminated) instance with its variables, jobs, incidents, user tasks and timers; a running instance answers 409 (cancel it first). The janitor does the same automatically after the retention period.
