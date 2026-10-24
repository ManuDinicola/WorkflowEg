## 21. Repository layout & milestones

### 21.1 Layout

```
flowd/
├─ src/
│  ├─ Flowd.Bpmn/              # parser, immutable model (closed hierarchy), validator
│  ├─ Flowd.Expressions/       # flowd-FEEL lexer/parser/evaluator, ExpressionValue union
│  ├─ Flowd.Engine/            # pure interpreter, IElementHandler<T> per element, unions (Section 2.3)
│  ├─ Flowd.Application/       # use cases, ports (IJobQueue, ITimerStore, ...), Result<T>
│  ├─ Flowd.Infrastructure/    # Npgsql/Dapper stores, DbUp migrations, schedulers, outbox, janitor
│  ├─ Flowd.Api/               # minimal API endpoints, middleware, auth, mapping
│  ├─ Flowd.Contracts/         # public DTOs shared by Api and SDK
│  ├─ Flowd.Worker.Client/     # C# worker SDK (M7)
│  └─ Flowd.Host/              # composition root + CLI (serve|migrate|validate)
├─ tests/
│  ├─ Flowd.<Project>.Tests/   # unit tests per project
│  ├─ Flowd.Conformance.Tests/ # BPMN fixture tests on in-memory engine + FakeTimeProvider
│  ├─ Flowd.Integration.Tests/ # Testcontainers Postgres, category "Integration"
│  └─ Flowd.Architecture.Tests/# NetArchTest rules (ARC-001)
├─ bench/                      # BenchmarkDotNet + load scripts (M7)
├─ sdk/ts/                     # optional TypeScript worker SDK (M7)
├─ db/migrations/              # forward-only SQL embedded by DbUp
├─ testdata/bpmn/              # fixtures (see Section 22)
├─ docs/spec/                  # split spec files + 00-index.md (PLAN-006)
├─ docs/  deploy/  scripts/
├─ Flowd.slnx  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
├─ DECISIONS.md  PLAN.md  PROGRESS.md  CLAUDE.md  Makefile  Dockerfile  docker-compose.yml
```

### 21.2 Milestones (each ends with all listed acceptance tests green in CI)

**M0 — Skeleton (½ week)**
New repository bootstrap (GIT-001a), then, as separate slices (PLAN-002): (1) split the spec into `docs/spec/` (PLAN-006) and create `PLAN.md`, `CLAUDE.md`, `DECISIONS.md`, `PROGRESS.md`; (2) `Flowd.slnx`, `global.json`, `Directory.Build.props` (`Nullable`, `TreatWarningsAsErrors`, `AnalysisMode=All`, `LangVersion` default for net11.0), `Directory.Packages.props`, `.editorconfig`, analyzers; (3) the project skeleton of Section 2.4 with the architecture tests (ARC-001) and the union/closed-hierarchy smoke test `T-BOOT-01` (UNI-001); (4) Makefile with the gate targets, CI (Section 21.3), Dockerfile, compose; (5) DbUp migration runner (`flowd migrate`), `/healthz`, `/readyz`. Work happens on branch `feat/m0-skeleton` (Section 0.1) or one branch per slice.
✔ `make ci` runs all gate targets and is green on an empty engine; `T-BOOT-01` passes; `flowd migrate` creates schema; `/healthz` 200; CI workflow runs on every push/PR.

**M1 — Model & deploy**
BPMN parser + validator (FR-DEP-004), definitions persistence & versioning (FR-DEP-001..003), `flowd validate`.
✔ ACC-M1: T-DEP-01..06 (Section 22).

**M2 — Core execution (linear + gateways)**
Interpreter, start/end/none events, service task + jobs (activate/complete/fail/timeout), exclusive & parallel gateways, expressions (8.2 core), variables & I/O mappings, instance cancel, history.
✔ ACC-M2: T-EXEC-01..12, T-JOB-01..08, T-EXPR-01..10.

**M3 — Structure & control**
Sub-processes, inclusive gateway, script/manual/send tasks, incidents + resolve, loop limit.
✔ ACC-M3: T-SUB-*, T-INC-*, T-INCL-*.

**M4 — Time & messages**
Timers (catch, boundary, start, cycle), message catch/boundary/start/receive task, buffered messages, event-based gateway.
✔ ACC-M4: T-TMR-*, T-MSG-*, T-EVG-*.

**M5 — Errors, signals, user tasks**
Error boundary/end events and propagation, signals, intermediate throw events, native user tasks (claim/assign/complete).
✔ ACC-M5: T-ERR-*, T-SIG-*, T-UT-*.

**M6 — Composition**
Call activity, multi-instance (parallel/sequential), event sub-processes.
✔ ACC-M6: T-CALL-*, T-MI-*, T-ESP-*.

**M7 — Production readiness**
AuthN/Z, multi-tenancy, outbox/webhooks, janitor/retention, metrics/tracing, rate limits, worker SDKs, benchmarks, chaos/crash tests, OpenAPI contract tests, docs.
✔ ACC-M7: NFR-001..008 verified; T-SEC-*, T-CHAOS-*.

### 21.3 Makefile targets & CI (required in M0)

**Makefile targets (names fixed so agents can rely on them):**

| Target | What it does |
|---|---|
| `make lint` | `dotnet format Flowd.slnx --verify-no-changes`, `dotnet build Flowd.slnx -warnaserror`, `Flowd.Architecture.Tests`, OpenAPI document lint |
| `make test-unit` | `dotnet test` on all `*.Tests` projects except Conformance, Integration and Architecture (no external services) |
| `make test-conformance` | `dotnet test tests/Flowd.Conformance.Tests`: all BPMN fixtures in `testdata/bpmn` against the in-memory engine with `FakeTimeProvider` |
| `make test-integration` | `dotnet test tests/Flowd.Integration.Tests` (Testcontainers starts Postgres; needs a Docker daemon) |
| `make test-repeat PROJECT=<p> N=<n>` | runs one test project `N` times to detect flakiness (TEST-005) |
| `make cover` | collects coverage with `dotnet-coverage`/coverlet, merges with ReportGenerator, fails if below NFR-006 thresholds |
| `make ci` | `lint` → `test-unit` → `test-conformance` → `test-integration` → `cover` (exactly what CI runs) |

The Makefile is a thin, stable wrapper over the `dotnet` CLI so agents and CI call the same commands. Quiet flags are used by default (`-v:q -clp:ErrorsOnly`, test logger verbosity `minimal`) to protect context (TOK-007).

**CI (GitHub Actions or equivalent), `.github/workflows/ci.yml`:**
- **CI-001** Triggers on every push to any branch and on every pull request to `main`.
- **CI-002** Uses `actions/setup-dotnet` with the SDK from `global.json`; runs `make ci`; Docker available for Testcontainers; matrix over Postgres 14 and 17 for integration tests (image tag passed by environment variable).
- **CI-003** Uploads coverage and test reports as artifacts; fails the check on any red or skipped-without-reason test.
- **CI-004** `main` is a protected branch: PRs require passing CI and one review before merge (configure in repository settings; document in `docs/contributing.md`).


