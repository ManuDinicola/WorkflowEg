# PLAN: Flowd (coarse plan, one branch + PR per milestone)

Decisions (2026-10-24, see `DECISIONS.md`): granularity **coarse**, parallel subagents for independent work, review stop **after every milestone**, remote `origin`, .NET 11 RC now / GA later.

| # | Milestone | Branch | Covers rows |
|---|---|---|---|
| [x] M0 | Skeleton & tooling | `feat/m0-skeleton` | 0.1–0.5 |
| [x] M1 | Model & deploy | `feat/m1-model-deploy` | 1.1–1.3 (+2.1 expressions in parallel) |
| [x] M2 | Core execution | `feat/m2-core-execution` | 2.2–2.9 |
| [ ] M3–M7 | as below | `feat/m<N>-<slug>` | refined at milestone start |

The rows below are the commit checklist inside each milestone branch (branch column = sub-branch for parallel subagents).

## 2. Rules for every slice

- Branch from up-to-date `main` (GIT-001/002); the only commit on `main` is the bootstrap (GIT-001a).
- Tests first or together (TEST-002); gate before commit and push: `make lint`, `make test-unit`, `make test-conformance`, `make test-integration` (when persistence/API/jobs/timers/messages touched), `make cover`.
- Executor gets only: its row below, the spec sections it names, `PROGRESS.md` (TOK-004 brief). Reports ≤ 150 words.
- A slice marked L is split before it starts (PLAN-002). Re-plan if a slice drifts (PLAN-007).
- Size: S ≈ <150 changed lines · M ≈ 150–400 · L = too big, split.

## 3. Slices

### M0 — Skeleton and tooling

| # | Slice | Requirement IDs | Tests | Branch | Size | Depends on | Executor |
|---|---|---|---|---|---|---|---|
| [x] 0.1 | `git init -b main`, first commit (`.gitignore`, README, LICENSE); split spec into `docs/spec/` + index; create `PLAN.md`, `CLAUDE.md`, `DECISIONS.md`, `PROGRESS.md` | GIT-001a, PLAN-006 | none (docs) | `chore/m0-bootstrap-spec-split` | S | — | main |
| [x] 0.2 | `Flowd.slnx`, `global.json` (RC SDK, rollForward latestFeature), `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, analyzers | §2.1, CC-010, CC-011 | `dotnet build -warnaserror` green | `chore/m0-solution-scaffold` | M | 0.1 | impl-worker |
| [x] 0.3 | Project skeleton (Section 2.4), architecture tests, union + closed-hierarchy smoke test | ARC-001, UNI-001..003 | T-BOOT-01, architecture tests | `feat/m0-project-skeleton` | M | 0.2 | impl-worker |
| [x] 0.4 | Makefile gate targets, CI workflow, Dockerfile, docker-compose | §21.3, CI-001..004 | `make ci` green locally and in CI | `chore/m0-ci-docker` | M | 0.3 | impl-worker |
| [x] 0.5 | DbUp runner, `flowd migrate`, `/healthz`, `/readyz` | FR-DB-001, API-013 | integration: migrate creates schema; health 200 | `feat/m0-migrations-health` | M | 0.4 | impl-worker |

### M1 — Model and deployment

| # | Slice | Requirement IDs | Tests | Branch | Size | Depends on | Executor |
|---|---|---|---|---|---|---|---|
| [x] 1.1 | BPMN immutable model (closed hierarchy) + XXE-safe parser for MVP elements | §6.1, FR-SEC-006 | T-DEP-06, parser unit tests | `feat/m1-bpmn-model-parser` | M | 0.3 | impl-worker |
| [x] 1.2 | Validator that collects all errors; `flowd validate` | FR-DEP-004 | T-DEP-04, T-DEP-05 | `feat/m1-bpmn-validator` | M | 1.1 | impl-worker |
| [x] 1.3 | Definitions store, checksum versioning, deployments + definitions API | FR-DEP-001..003, FR-DEP-005, API-001, API-002 | T-DEP-01..03 | `feat/m1-deployments` | M | 1.1, 0.5 | impl-worker |

### M2 — Core execution (linear flow, gateways, jobs)

| # | Slice | Requirement IDs | Tests | Branch | Size | Depends on | Executor |
|---|---|---|---|---|---|---|---|
| [x] 2.1 | flowd-FEEL lexer/parser/evaluator, `ExpressionValue` union, budget | §8.1, §8.2, FR-EXPR-SEC-001 | T-EXPR-01..10 | `feat/m2-expressions` | M | 0.3 | impl-worker (parallel with M1) |
| [x] 2.2 | Engine state, key generator, `Step`, `IElementHandler<T>` registry, start/end events | FR-ARCH-001, FR-ARCH-006, FR-EXEC-001, FR-EXEC-002, FR-EXEC-008, CC-003 | T-EXEC-01 | `feat/m2-engine-core` | M | 1.1 | Plan (opus) then impl-worker |
| [x] 2.3 | Exclusive and parallel gateways | FR-EXEC-003, FR-EXEC-004 | T-EXEC-03..07 | `feat/m2-gateways` | M | 2.1, 2.2 | impl-worker |
| [x] 2.4 | Variables, scopes, input/output mappings | FR-VAR-001..003, §8.4 | T-EXEC-10 | `feat/m2-variables-mappings` | M | 2.1, 2.2 | impl-worker (parallel with 2.3) |
| [x] 2.5a | Postgres schema + stores for instances, elements, variables, history | §15, FR-HIS-001 | integration | `feat/m2-stores` | M | 0.5, 2.2 | impl-worker |
| [x] 2.5b | Command pipeline: transaction, instance row lock, lock ordering, persistence of step outcome | FR-ARCH-002, FR-ARCH-003, FR-CON-001..003 | integration (concurrency) | `feat/m2-command-pipeline` | M | 2.5a | impl-worker |
| [x] 2.6a | Service task handler: job creation, completion, failure semantics (engine side) | FR-EXEC-006, FR-JOB-003, FR-JOB-005 | T-EXEC-02 | `feat/m2-service-task` | M | 2.2, 2.4 | impl-worker |
| [x] 2.6b | Job store, activation (`SKIP LOCKED`, long poll), timeout sweeper, job API | FR-JOB-001..009, API-007, API-008, FR-CON-004, FR-CON-005 | T-JOB-01..08 | `feat/m2-job-api` | L→split in two at start | 2.5b, 2.6a | impl-worker |
| [x] 2.7 | Start, cancel, query, set-variables API; history endpoint | FR-EXEC-015, API-003..006, API-012 | T-EXEC-08, T-EXEC-09 | `feat/m2-instances-api` | M | 2.5b | impl-worker |
| [x] 2.8 | Determinism property test and loop limit | FR-EXEC-016, FR-ARCH-006 | T-EXEC-11, T-EXEC-12 | `test/m2-determinism-looplimit` | S | 2.3 | impl-worker |
| [x] 2.9 | Milestone review: reviewer subagent vs CC-001..018, ACC-M2 run, update docs | CC-018 | ACC-M2 | `docs/m2-review` | S | all M2 | reviewer (opus) |

### M3 — Structure and control (refine at milestone start)

| # | Slice | Requirement IDs | Tests | Size | Executor |
|---|---|---|---|---|---|
| [x] 3.1 | Embedded sub-process and scope handling | FR-EXEC-010 | T-SUB-01 | M | impl-worker |
| [x] 3.2 | Inclusive gateway + reachability doc `docs/inclusive-join.md` | FR-EXEC-005 | T-INCL-01, T-INCL-02 | M | Plan (opus) then impl-worker |
| [x] 3.3 | Script, manual, send tasks | FR-EXEC-007 | unit + conformance | S | impl-worker |
| [x] 3.4 | Incidents: raise, list, resolve; incident API | FR-INC-001..003, API-010 | T-INC-01 | M | impl-worker |

### M4 — Time and messages (refine at milestone start)

| # | Slice | Requirement IDs | Tests | Size | Executor |
|---|---|---|---|---|---|
| [x] 4.1 | Timer store, scheduler `BackgroundService`, cycle re-arming | FR-TMR-001..006 | T-TMR-04, T-TMR-05 | M | impl-worker |
| [x] 4.2 | Timer catch and boundary events (interrupting / non-interrupting) | FR-EXEC-013 | T-TMR-01..03 | M | impl-worker |
| [x] 4.3 | Message store, buffering, correlation, publish API | FR-MSG-001..005, API-009 | T-MSG-01..05 | M | impl-worker |
| [x] 4.4 | Message catch/boundary/start events, receive task | FR-MSG-004 | T-MSG-04 | M | impl-worker |
| [x] 4.5 | Event-based gateway | FR-EXEC-014 | T-EVG-01 | S | impl-worker |

### M5 — Errors, signals, user tasks (refine at milestone start)

| # | Slice | Requirement IDs | Tests | Size | Executor |
|---|---|---|---|---|---|
| [x] 5.1 | BPMN error throw and propagation (`ErrorHandlerMatch` union) | §12.3 | T-ERR-01..03 | M | Plan (opus) then impl-worker |
| [x] 5.2 | Signals (broadcast, catch, boundary) | FR-SIG-001 | T-SIG-01 | S | impl-worker |
| [x] 5.3 | Intermediate throw events | §6.1 | conformance | S | impl-worker |
| [x] 5.4 | Native user tasks + API | API-011 | T-UT-01 | M | impl-worker |

### M6 — Composition (refine at milestone start)

| # | Slice | Requirement IDs | Tests | Size | Executor |
|---|---|---|---|---|---|
| [x] 6.1 | Call activity, variable propagation, cascade cancel | FR-EXEC-011, FR-EXEC-015 | T-CALL-01, T-CALL-02 | M | impl-worker |
| [x] 6.2 | Multi-instance (parallel, sequential, empty collection, ordering) | FR-EXEC-012 | T-MI-01..03 | M | Plan (opus) then impl-worker |
| [x] 6.3 | Event sub-processes | §6.1 | T-ESP-01 | M | impl-worker |

### M7 — Production readiness (refine at milestone start)

| # | Slice | Requirement IDs | Tests | Size | Executor |
|---|---|---|---|---|---|
| [x] 7.1 | AuthN/Z (JWT, API keys, roles) and tenancy | FR-SEC-001..003 | T-SEC-01, T-SEC-02 | M | impl-worker |
| [x] 7.2 | Outbox publisher + signed webhooks | FR-HIS-002, FR-ARCH-005 | integration | M | impl-worker |
| [x] 7.3 | Janitor / retention | FR-DB-002 | integration | S | impl-worker |
| [x] 7.4 | Metrics, tracing, logging polish | FR-OBS-001..003 | unit | M | impl-worker |
| [x] 7.5 | Rate limits, idempotency keys, input limits | API-R1, FR-SEC-004 | unit + integration | M | impl-worker |
| [x] 7.6 | `Flowd.Worker.Client` SDK (+ optional TS SDK) | §9.2 | SDK tests | M | impl-worker |
| [x] 7.7 | BenchmarkDotNet + load scripts; union boxing check | NFR-001, NFR-002, UNI-005 | bench report | M | impl-worker |
| [x] 7.8 | Chaos / crash recovery tests | NFR-003, NFR-005 | T-CHAOS-01 | M | impl-worker |
| [x] 7.9 | OpenAPI contract tests, docs, runbook | API-R3, NFR-008 | contract tests | M | impl-worker |

## 4. Kickoff prompt (paste into Claude Code in the empty repo folder)

```
Read CLAUDE.md or, if it does not exist yet, WORKFLOW_ENGINE_SPEC.md sections 0, 0.1, 0.2, 0.3 and PLAN.md only.
Do not read the rest of the spec. Enter plan mode, confirm or adjust the slice table in PLAN.md for milestone M0,
ask me the five questions in PLAN.md section 1 (use AskUserQuestion), then execute slice 0.1 on its own branch.
After each slice: run the gate, update PLAN.md and PROGRESS.md, push, open a PR, and stop for review per my answer.
Use subagents for exploration, test runs and independent slices as described in spec section 0.2.
```


---

## Beyond the original spec: Camunda parity and more (added 2026-10-24 at the user's request)
One branch and PR per milestone, stacked on the previous one. Each item needs conformance tests and a README/docs update.

### M8 - BPMN completeness (feat/m8-bpmn-completeness)
| # | Item | Notes |
|---|---|---|
| [ ] 8.1 | Escalation events (throw, boundary, event sub-process) | like error, but non-fatal when uncaught |
| [ ] 8.2 | Message throw / send task, intermediate message throw | publishes a message to the correlation store |
| [ ] 8.3 | Link events, conditional events (boundary, catch, start) | |
| [ ] 8.4 | Terminate end event, multiple/parallel-multiple events, none-end variants | |
| [ ] 8.5 | Compensation (compensate events, handlers, transactions, cancel) | |
| [ ] 8.6 | Complex gateway, ad-hoc sub-process | |
| [ ] 8.7 | Event sub-process variants (all trigger types), timer cycles on start | |

### M9 - Expressions and decisions
| [ ] 9.1 | Full FEEL (ranges, filters, path, for/if/some/every, functions, temporal) | replaces the subset |
| [ ] 9.2 | DMN 1.3: decision tables (all hit policies), literal expressions, DRG, business rule task | |

### M10 - Operations on running instances
| [ ] 10.1 | Process instance modification (move/add/cancel tokens) | |
| [ ] 10.2 | Process instance migration between versions | |
| [ ] 10.3 | Batch operations (cancel, resolve incidents, migrate, modify) | |
| [ ] 10.4 | Variable and job update APIs, job streaming, search/filter queries with paging | |

### M11 - Human work and forms
| [ ] 11.1 | Forms (JSON schema form definitions linked to user tasks), candidate groups/users, due dates, follow-ups, priorities | |
| [ ] 11.2 | Task search/assignment API, task listeners | |

### M12 - Connectors and SDKs
| [ ] 12.1 | Built-in connectors: REST, webhook inbound, e-mail, queue | |
| [ ] 12.2 | TypeScript worker SDK; OpenAPI client generation | |
| [ ] 12.3 | gRPC or SSE job streaming | |

### M13 - Web UI and analytics
| [ ] 13.1 | Operate-like UI (definitions, instances, incidents, variables, history, diagram with token overlay) | |
| [ ] 13.2 | Tasklist-like UI | |
| [ ] 13.3 | Browser BPMN modeler/deploy page | |
| [ ] 13.4 | Analytics (durations, counts, heatmap) | |

### M14 - Scale and hardening
| [ ] 14.1 | Partitioned execution / multi-node scaling tests, load tests with published numbers | |
| [ ] 14.2 | Union boxing check (UNI-005), full chaos suite, soak test | |
| [ ] 14.3 | Multi-tenancy admin, audit log, secrets handling | |
