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

## Audit log
Every mutating API call (POST, PUT, PATCH, DELETE) is recorded with time, caller (API key name or JWT subject), tenant, method, path and status. Bodies and variable values are never stored. Admins read it with `GET /v1/audit?after=<seq>&pageSize=100`. A failing audit write is logged and does not fail the request. The audit table is not purged by the janitor yet; archive or truncate it with your backup routine.

## Event stream
`GET /v1/events/stream` is a server-sent-events stream of the lifecycle events (`PROCESS_INSTANCE_STARTED`, `ELEMENT_ACTIVATED`, ... see the history) of all instances of the caller's tenant, in order. Each event has `id: <seq>`; reconnect with `?after=<seq>` or the `Last-Event-ID` header to resume without gaps. Use it for dashboards or to feed your own systems; the signed webhooks remain the reliable push channel.
```
curl -N -H "X-Api-Key: ..." http://localhost:8080/v1/events/stream
```

## Fixing a stuck or wrong instance (modification)
List what is running: `GET /v1/process-instances/{key}/elements` (element instance keys, element ids, scope).
Move tokens: `POST /v1/process-instances/{key}/modify` with
```
{ "terminateElementInstanceKeys": ["372..."], "activateElementIds": ["Review"], "variables": { "approved": true } }
```
The listed element instances are ended (with everything inside them), the listed elements are started in their scope (the process root, or the running sub-process that contains them), and the variables are set on the instance. Boundary events and event sub-processes cannot be activated directly. A finished instance answers 409.

Start somewhere else: `POST /v1/process-instances` with `"startElementId": "Review"` starts a new instance at any element of the process root instead of the start event.

## Migrating running instances to a new version
Deploy the new version, then move instances over:
```
POST /v1/process-instances/{key}/migrate
{ "targetDefinitionKey": "372...", "mapping": { "ReviewOld": "ReviewNew" } }
```
Rules: the target must be another deployed version of the same process; the instance must be active. Every element the instance is waiting on, and every timer or message trigger it holds, must exist in the target version, under the same id or through `mapping`; running elements must keep their kind (a service task cannot become a user task). If anything is missing the answer is 400 with the list of problems and nothing is changed. Job types are not rewritten: jobs already created keep their type; new steps use the new version.

## Batch operations
Each runs the single-item operation for up to 1000 matches, one by one; one failure does not stop the rest, and `truncated: true` means more matches remain (call again).
- `POST /v1/batch/cancel-instances` `{ "bpmnProcessId": "order", "maxItems": 500 }`
- `POST /v1/batch/resolve-incidents` `{ "processInstanceKey": "...", "maxItems": 500 }` (an incident whose job has no retries left needs `PATCH /v1/jobs/{key}` with retries first)
- `POST /v1/batch/migrate-instances` `{ "bpmnProcessId": "order", "targetDefinitionKey": "...", "mapping": { } }`

## Job streaming
Instead of polling, a worker can hold one connection open: `GET /v1/jobs/stream?type=charge-card&worker=w1&timeoutMs=30000&maxJobs=10&fetchVariables=amount,card` answers with server-sent events (`event: job`, `data:` a job as in the activate response; `: keep-alive` comments while idle). Report results with the normal complete / fail / error calls. A job pushed to a connection that dies is handed out again after `timeoutMs`.

## Search
- `GET /v1/jobs?type=&state=&processInstanceKey=&worker=` finds jobs (for example `state=ACTIVATED` jobs of a stuck worker).
- `GET /v1/process-instances?variable=customer:acme&order=desc` filters by a root variable (the value is JSON, or text taken as a string) and lists newest first; the usual `state`, `bpmnProcessId`, `businessKey`, `startedAfter`, `startedBefore` still apply.

## Limiting an API key to some processes
`flowd apikey create --name ci --roles developer --processes order,invoice` (JWT: add `flowd:processes` claims). Such a key can deploy only those process ids, start only those, and run batch operations only on those (batch cancel must name a process). Reading or changing a single instance by key is not restricted by process yet; use tenants for hard separation.

## gRPC API
Besides REST, Flowd serves `flowd.v1.Gateway` (`StartInstance`, `ActivateJobs`, `CompleteJob`, `FailJob`, `PublishMessage`; the contract is `src/Flowd.Api/Grpc/flowd.proto`). It uses the same authentication (send the API key or JWT as call metadata, `X-Api-Key` / `authorization`), roles, tenants and process limits as REST. Variables are JSON text. Failures are gRPC status codes (`NOT_FOUND`, `FAILED_PRECONDITION`, `PERMISSION_DENIED`, `INVALID_ARGUMENT`, `RESOURCE_EXHAUSTED`, `UNAVAILABLE`).

gRPC needs HTTP/2. Behind TLS the normal endpoint works. Without TLS, Kestrel serves HTTP/2 only on an endpoint that says so, for example `Kestrel__Endpoints__Rest__Url=http://0.0.0.0:8080`, `Kestrel__Endpoints__Rest__Protocols=Http1`, `Kestrel__Endpoints__Grpc__Url=http://0.0.0.0:8081`, `Kestrel__Endpoints__Grpc__Protocols=Http2`.

## Start an instance and wait for the result
`POST /v1/process-instances` with `"awaitResult": true` (and optionally `"requestTimeoutMs"`, default 10000, at most 60000, and `"fetchVariables": ["a","b"]`) waits until the instance has ended and answers with `state` and, when it completed, its `variables` (only the fetched ones if given). If the instance is still active when the wait ends, the answer is `200` with `"state": "ACTIVE"` and no variables: the instance keeps running and you can query it by key. Without the flag the answer is as before.

## Managing API keys
Besides `flowd apikey create`, administrators (role `admin`) manage keys through the API and the **Access** tab of the UI: `GET /v1/api-keys` lists keys (id, name, roles, tenant, process limits, created, revoked; never the key), `POST /v1/api-keys` with `{"name","roles":[...],"tenant","processes":[...]}` creates one and returns the key **once**, `DELETE /v1/api-keys/{id}` revokes it at once (the id is the first 12 hex characters of the key hash). An admin bound to a tenant sees and changes only keys of its own tenant, and every key it creates belongs to that tenant whatever the request says. Roles are admin, operator, developer, worker and reader. People who sign in through your identity provider are managed there, not in Flowd.
