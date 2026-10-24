# Architecture

Dependencies point inward. This is enforced by `tests/Flowd.Architecture.Tests` (NetArchTest), which runs in `make lint` and in CI:

```
Bpmn  <-  Engine  <-  Application  <-  Infrastructure / Api  <-  Host
Expressions <- Engine          Contracts (DTOs)  <-  Api, Worker.Client
```

| Project | Role |
|---|---|
| `Flowd.Bpmn` | BPMN model, XXE-safe parser, validator with stable issue codes |
| `Flowd.Expressions` | flowd-FEEL subset (lexer, parser, evaluator) with step and time budgets |
| `Flowd.Engine` | **Pure** interpreter: `Execute(state, command, now, keys)` returns effects or a rejection. No I/O, clock, randomness |
| `Flowd.Application` | Use cases and ports: command pipeline, jobs, timers, messages, user tasks, retention, security ports |
| `Flowd.Infrastructure` | PostgreSQL adapters (Dapper/Npgsql), DbUp migrations, background services (timers, job timeouts, outbox, janitor) |
| `Flowd.Api` | Minimal-API endpoints, auth policies, idempotency, rate limiting, problem-details mapping |
| `Flowd.Contracts` | Wire DTOs shared by the API and the worker SDK |
| `Flowd.Worker.Client` | Typed HTTP client and long-polling `JobWorker` |
| `Flowd.Host` | Composition root and CLI (`serve`, `migrate`, `validate`, `apikey create`) |

## Key design points
- **Unions for closed sets**: `EngineCommand`, `EngineEffect`, `Result<T>`, `JobResult`. Every switch is exhaustive; there are no `_ =>` arms (CS8509 is an error).
- **One transaction per command**: the instance row is locked (`FOR UPDATE`), the engine computes effects, effects are written in one batch, then post-commit effects (signals, child instances, buffered messages) run in their own transactions. A crash anywhere leaves a consistent state.
- **Jobs and timers** are leased with `SKIP LOCKED`, so several engine nodes can run against one database.
- **Outbox**: lifecycle events are written in the same transaction and delivered to a webhook with an HMAC-SHA256 signature (`X-Flowd-Signature`).
- **Security**: API keys (hashed) or JWT, roles, tenant isolation (cross-tenant access answers 404). Authentication is on by default.
- **Observability**: `/metrics` (Prometheus text), optional OTLP traces, JSON logs.

## Tests
- Architecture tests (rules above), unit tests per project, conformance tests (engine behaviour with an in-memory store and a fake clock), integration tests against real PostgreSQL (API, security, outbox, retention, worker SDK, OpenAPI contract, chaos).

Decisions and trade-offs are recorded in `DECISIONS.md`.
