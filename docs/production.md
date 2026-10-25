# Going to production

A checklist for putting Flowd on a real server, and an honest list of what is not proven. Details of each step are in `docs/operations.md`.

## Before the first start
- [ ] **Authentication on.** Never set `Flowd:Auth:Insecure=true` outside a developer machine. Create an admin API key with `flowd apikey create`, then create the other keys (workers, operators) through the Access tab or `POST /v1/api-keys`. Give every worker a key with the `worker` role and, where you can, a process limit.
- [ ] **TLS in front.** Flowd speaks plain HTTP. Put a reverse proxy (IIS, nginx, Caddy) with a certificate in front of it. For gRPC the proxy must speak HTTP/2 to the browser or client side; see "gRPC API" in `docs/operations.md`.
- [ ] **Secrets outside files.** Connection string, JWT signing key, SMTP password, webhook secrets and connector secrets go into environment variables or your secret store, never into `appsettings.json` that you commit.
- [ ] **PostgreSQL.** Supported 14 to 17. Use a dedicated database and role. Npgsql opens up to 100 connections per node unless the connection string sets `Maximum Pool Size`, so set `max_connections` to at least `nodes × pool size + 20`; the load tests used a pool of 30 per node. Put the data directory on fast disks; the engine commits on every command.
- [ ] **Backups scheduled and restored once.** Use `scripts/backup.ps1` or your own `pg_dump` / continuous archiving, and do a restore into an empty database before you rely on it. Check the restored database with `flowd migrate` (it should report nothing to do) and start a node against it.
- [ ] **Retention.** Decide how long finished instances and history stay (`Flowd__Retention__*`, defaults 30 days for instances and history), otherwise the database grows without limit.
- [ ] **Rate limit.** The default is 500 requests per second per node with a burst of 1000. Raise it for busy worker fleets (`Flowd__RateLimit__*`) or workers will see `429`.
- [ ] **Single sign-on.** If people use the UI, register a public client at your identity provider and set `Flowd:Ui:Oidc:ClientId` (see `docs/ui.md`). Test it once with your real provider: Flowd's own tests use a mock provider.

## Monitoring
- Scrape `/metrics` (Prometheus). Useful alerts:
  - `flowd_incidents_open` above 0 for more than a few minutes.
  - `flowd_timers_due_lag_seconds` above 30 (timers are late: a node is down or the database is slow).
  - `flowd_job_activation_latency_seconds` p95 rising, or `flowd_jobs_timed_out_total` growing (workers too slow or dead).
  - `flowd_command_lock_wait_seconds` p95 above a second (many commands on the same instance).
- `/healthz` (process alive) and `/readyz` (database reachable and migrated) for your load balancer.
- Logs are JSON on the console. Ship them somewhere you can search. Variable values are not logged at information level.

## Upgrades
1. Take a backup.
2. Stop traffic to one node, deploy the new binary, run `flowd migrate` once (migrations are forward-only and idempotent), start the node.
3. Roll through the other nodes. Do not assume the old and the new version can share a migrated database: check the release notes of each release, and when in doubt stop all nodes, migrate, then start them.
4. Running instances keep their process version; migrate them deliberately with the migration API when you want them on the new one.

## What to test on your own server before trusting it
- **Load.** `bench/Flowd.Load` against your hardware with your real process shapes. The published numbers (`docs/benchmarks.md`) were measured on one development machine.
- **Failure.** Kill a node during load (jobs must be retried, none lost: at-least-once means a job can run twice, so workers must be idempotent), restart PostgreSQL during load, fill the disk of a test database.
- **Restore.** Restore last night's backup and run a real process on it.

## Known limits and differences (read this)
- **No production history.** Flowd is new. Camunda has years of field use; Flowd has automated tests (unit, conformance, integration on PostgreSQL 14 and 17, a chaos test) and a few load runs.
- **One PostgreSQL.** Nodes scale the engine, not the database. There is no partitioning and no built-in database failover; use your PostgreSQL high-availability setup. Scaling across separate machines has not been demonstrated.
- **Idempotency keys** are stored after the command commits, so a crash in that gap can let a retried request run twice.
- **At-least-once everywhere.** Jobs, connectors (HTTP, e-mail), webhooks and message correlation can repeat after failures. Make the called systems idempotent.
- **Differences from Camunda 8** are listed in `docs/camunda-parity.md` (listeners do not block, compensation is simplified, no conditional start events, no message queue connector, user defined functions only inside one expression, people and groups are managed in your identity provider).
- **UI sessions.** The sign-in keeps an access token in the browser tab and has no refresh token: people sign in again when it expires.
- **Web UI is built in and simple.** It is meant for operating and light modeling; large teams will want version control for their models (keep your `.bpmn`, `.dmn` and `.form` files in git and deploy from CI).
- **Waiting starts.** A start request with `awaitResult` waits at most 60 seconds and at most 64 such waits run at once per node; more answer immediately without waiting.

## Reporting a problem
Keep the instance key, the time, and the output of `GET /v1/process-instances/{key}/history`. The audit log (`GET /v1/audit`) shows who changed what.
