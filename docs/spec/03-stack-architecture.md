## 2. Technology baseline, language rules & code quality

This is a **new repository**: everything below is created from scratch in milestone M0 (Section 21).

### 2.1 Stack (defaults; deviations only via `DECISIONS.md`)

| Concern | Decision |
|---|---|
| Runtime / SDK | **.NET 11** (`net11.0`), the current **STS** (non-LTS) release. Not .NET 10 LTS. |
| Language | **C# 15** (default for `net11.0`). Union types (`union` keyword) and, where useful, closed hierarchies are **required** where Section 2.3 says so. |
| SDK pinning | `global.json` pins the SDK with `"rollForward": "latestFeature"`. Until .NET 11 GA (expected November 2026) use the newest RC SDK (go-live licensed); once GA ships, pin the GA SDK and the `11.0` container tags. Record the pinned version in `DECISIONS.md`. |
| Solution format | `Flowd.slnx` (XML solution), central package management (`Directory.Packages.props`), shared settings in `Directory.Build.props` |
| Database | PostgreSQL 16 (only supported DB in v1) via **Npgsql** + **Dapper** (explicit SQL; required for `FOR UPDATE SKIP LOCKED`, `LISTEN/NOTIFY`, `jsonb`) |
| Migrations | **DbUp** running plain, forward-only SQL files embedded from `db/migrations` |
| HTTP | ASP.NET Core **Minimal APIs** + endpoint groups; built-in OpenAPI document generation (`Microsoft.AspNetCore.OpenApi`); built-in `ProblemDetails`, rate limiting, health checks, JWT bearer auth |
| XML parsing | `System.Xml.Linq` with `DtdProcessing.Prohibit`, `XmlResolver = null`, validated against the BPMN 2.0 XSD |
| JSON | `System.Text.Json` (`JsonNode`/`JsonElement`, source-generated contexts); no Newtonsoft |
| DI / config | `Microsoft.Extensions.*` (DI, Options with validation `ValidateOnStart`, Hosting); env vars + `appsettings.json`; 12-factor |
| CLI | `System.CommandLine`: `flowd serve`, `flowd migrate`, `flowd validate <file.bpmn>` |
| Background work | `BackgroundService` per scheduler (timers, job-timeout sweeper, outbox, janitor, message TTL) |
| Observability | `System.Diagnostics.Metrics` (`Meter`) + `ActivitySource` exported with OpenTelemetry .NET; logs via `ILogger` with **source-generated** `[LoggerMessage]` and JSON console formatter |
| Time & IDs | `TimeProvider` (built in) and an `IKeyGenerator` port; never `DateTime.UtcNow`/`DateTimeOffset.UtcNow` outside the composition root |
| Tests | **xUnit v3**, **Shouldly** (assertions), **NSubstitute** (only for true boundaries), **FsCheck** (property tests), **Verify** (snapshot/golden BPMN history), **Testcontainers for .NET** (Postgres), **NetArchTest.Rules** (architecture), `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) |
| Static analysis | .NET analyzers at `AnalysisMode=All`, **Roslynator**, **Meziantou.Analyzer**, **SonarAnalyzer.CSharp**; `TreatWarningsAsErrors=true` |
| Coverage | `coverlet`/`dotnet-coverage` + ReportGenerator with enforced thresholds (NFR-006) |
| Packaging | multi-stage `Dockerfile` (SDK image → `mcr.microsoft.com/dotnet/aspnet:11.0` runtime, non-root user) + `docker-compose.yml` (engine + postgres) |

**Process model:** one host `flowd` (project `Flowd.Host`) with subcommands `flowd serve` (API + engine + schedulers), `flowd migrate`, `flowd validate <file.bpmn>`.

### 2.2 Clean code & SOLID rules (normative, enforced by analyzers + review)

- **CC-001 Architecture.** Clean/Hexagonal architecture with the **dependency rule**: dependencies point inward only. `Flowd.Engine` (pure core) knows nothing about Postgres, ASP.NET, JSON transport or the clock. Adapters implement ports declared by the inner layers.
- **CC-002 Single Responsibility.** One reason to change per class. Limits (analyzer-enforced where possible): method ≤ 25 lines, cyclomatic complexity ≤ 10, class ≤ 300 lines, ≤ 5 constructor dependencies, ≤ 4 parameters per method (otherwise introduce a parameter object / `record`).
- **CC-003 Open/Closed.** Each BPMN element behavior is its own `IElementHandler<TElement>` (e.g. `ParallelGatewayHandler`, `ServiceTaskHandler`), registered in DI and resolved by a handler registry. Adding an element MUST NOT require editing the interpreter loop or existing handlers.
- **CC-004 Liskov.** No `NotImplementedException`, no handler that narrows a contract; interfaces are small and behavior-complete.
- **CC-005 Interface Segregation.** Ports are narrow and role-based (`IJobQueue`, `IJobCompletionStore`, `ITimerStore`, `IMessageCorrelator`, `IProcessInstanceLock`), not one big `IRepository`.
- **CC-006 Dependency Inversion.** Constructor injection only; no service locator, no static mutable state, no `new` of infrastructure inside domain/application code. The composition root is `Flowd.Host`.
- **CC-007 Immutability & types.** Domain model uses `record`/`readonly record struct`/`init`-only members. **No primitive obsession**: `readonly record struct ProcessInstanceKey(long Value)`, `ElementId`, `JobKey`, `CorrelationKey`, `TenantId`, `BpmnProcessId`, etc. with validation in the constructor/factory.
- **CC-008 Errors.** Expected failures (validation, "job not activated", "no outgoing flow matched") are returned as values (`Result<T>` union, Section 2.3), not thrown. Exceptions only for programmer errors and infrastructure faults. Never catch `Exception` without rethrow or logging at a boundary; never swallow.
- **CC-009 Async & cancellation.** Async all the way (`Task`/`ValueTask`), every async method takes a `CancellationToken` (last parameter), no `.Result`/`.Wait()`/`async void`, `ConfigureAwait(false)` in library projects.
- **CC-010 Nullability & defaults.** `<Nullable>enable</Nullable>` everywhere, no `!` null-forgiving without a justification comment, `sealed` classes by default, `internal` by default (`public` only for the API surface of each project).
- **CC-011 Naming & style.** `.editorconfig` committed in M0 (file-scoped namespaces, `var` only when type is obvious, `_camelCase` private fields, `I`-prefixed interfaces, `Async` suffix, no abbreviations except well-known ones such as `Id`, `Json`). Intention-revealing names; comments explain *why*, not *what*; public API has XML docs (`GenerateDocumentationFile`).
- **CC-012 No magic values.** Status strings, event types, error codes and table/column names live in constants/enums/unions in one place (e.g. `ErrorCodes`, `HistoryEventTypes`).
- **CC-013 Guard clauses & early return** instead of deep nesting; prefer `switch` expressions and pattern matching over `if/else` chains.
- **CC-014 Logging.** Source-generated `[LoggerMessage]` only; structured placeholders; never log variable values at Information (FR-SEC-005); correlation via `Activity`.
- **CC-015 YAGNI / DRY.** Build only what the current milestone requires; extract duplication on the third occurrence (rule of three); no speculative abstractions.
- **CC-016 Tell, don't ask / composition over inheritance.** Behavior lives with the data it governs; inheritance only for closed hierarchies (Section 2.3) and never deeper than one level.
- **CC-017 Configuration.** Strongly typed options classes validated at startup (`ValidateDataAnnotations().ValidateOnStart()`); no `IConfiguration` access outside the composition root and options setup.
- **CC-018 Review checklist.** The `reviewer` subagent (Section 0.2) checks CC-001..017 on every diff and reports violations by ID.

### 2.3 C# 15 union types (required usage)

C# 15 (shipping with .NET 11) adds the `union` keyword: a union declares a **closed set of case types** (they need not share a base type), and `switch` over it must be exhaustive. Syntax per the current language preview/RC documentation:

```csharp
public union Result<T>(T, Failure);
public union EngineCommand(StartInstance, CompleteJob, FailJob, ThrowJobError, CancelInstance,
                           TriggerTimer, CorrelateMessage, BroadcastSignal, ResolveIncident, SetVariables);
```

- **UNI-001 Verify first (M0, test `T-BOOT-01`).** Union syntax and runtime attributes were still moving during the .NET 11 previews; the RC1 notes describe them as stabilized, but the agent MUST write a compile-and-run smoke test (declare a union, construct each case, exhaustive `switch`, non-exhaustive `switch` produces a diagnostic) before relying on them. If the compiler still needs a feature flag, set `<LangVersion>preview</LangVersion>` in `Directory.Build.props` for that case only and record it in `DECISIONS.md`. If the syntax differs from the examples here, follow the compiler and update this section.
- **UNI-002 Where unions MUST be used** (instead of marker interfaces, `object`, `OneOf`, nullable-flag pairs or exceptions):
  | Union | Cases (illustrative) |
  |---|---|
  | `Result<T>` | `T`, `Failure` — every application/use-case return that can fail in an expected way |
  | `Failure` | `ValidationFailure`, `NotFound`, `Conflict` (e.g. `JobNotActivated`), `PermissionDenied`, `RateLimited`, `Unavailable` (mapped to API-R4 error codes in exactly one place) |
  | `EngineCommand` | one record per command in Section 7 |
  | `EngineEffect` | `PersistElementChange`, `CreateJob`, `CreateTimer`, `OpenSubscription`, `AppendHistory`, `AddOutbox`, `RaiseIncident`, `ScheduleFollowUp` |
  | `StepOutcome` | `Continued`, `Waiting`, `Completed`, `IncidentRaised`, `Terminated` |
  | `ExpressionValue` | `NullValue`, `bool`, `decimal`, `string`, `DateTimeOffset`, `TimeSpan`, `ListValue`, `ContextValue` |
  | `ConditionOutcome` | `Matched`, `NotMatched`, `NotBoolean(ExpressionValue)`, `EvaluationFailed(Failure)` |
  | `FlowDecision` (gateways) | `TakeFlows(IReadOnlyList<FlowId>)`, `TakeDefault(FlowId)`, `NoFlowMatched` |
  | `ErrorHandlerMatch` (FR-EXEC §12.3) | `BoundaryEvent`, `EventSubProcess`, `CallActivityParent`, `Unhandled` |
  | `JobCompletionOutcome` | `Completed`, `AlreadyCompleted`, `NotActivated`, `Stale` |
- **UNI-003 Exhaustiveness is a build gate.** Every `switch` over a union MUST be exhaustive **without** a discard/default arm (`_ =>`), so adding a case breaks the build in every place that must handle it (warning CS8509 is promoted to an error via `TreatWarningsAsErrors`; add `<WarningsAsErrors>CS8509</WarningsAsErrors>` explicitly). A `default`/`_` arm over a union is a review failure.
- **UNI-004 Closed hierarchies for the BPMN model tree.** The parsed model (`FlowNode` → `Event`, `Task`, `Gateway`, `SubProcess`, `CallActivity`…) is a *family of types owned by the assembly*; use a C# 15 **closed class hierarchy** if available in the pinned SDK (verified by `T-BOOT-01`), otherwise a `sealed`-leaf abstract record hierarchy plus an architecture test that forbids subclasses outside `Flowd.Bpmn`. Unrelated case sets (Section UNI-002) use `union`.
- **UNI-005 Storage & performance.** Unions currently compile to a struct holding a single `object` reference and box value-type cases. On hot paths (`ExpressionValue`, `EngineEffect` allocation in the step loop) the agent MUST benchmark (BenchmarkDotNet project in `bench/`, M7) and, if boxing is measurable, use reference-type (`sealed record`) cases or a custom non-boxing union pattern — never abandon exhaustiveness.
- **UNI-006 Boundaries.** Unions MUST NOT leak into public HTTP/JSON contracts or DB columns. Map them to explicit DTOs (`System.Text.Json` with a `type` discriminator property) at the adapter edge; DTOs are plain records.

### 2.4 Solution layout, dependency rules & enforcement

Projects (all `net11.0`, `src/` unless noted); arrows mean "may reference":

```
Flowd.Host ──▶ Flowd.Api ─────────┐
     │                            ▼
     ├──────▶ Flowd.Infrastructure ──▶ Flowd.Application ──▶ Flowd.Engine ──▶ Flowd.Bpmn
     └──────▶ Flowd.Application      (Flowd.Expressions is referenced by Engine)
Flowd.Worker.Client  (standalone SDK, references only Flowd.Contracts)
Flowd.Contracts      (public request/response DTOs shared by Api and SDK)
```

- `Flowd.Bpmn` — BPMN XML parser, immutable model, validator (FR-DEP-004). No I/O besides the stream passed in.
- `Flowd.Expressions` — flowd-FEEL lexer/parser/evaluator (Section 8). Pure.
- `Flowd.Engine` — pure interpreter and element handlers (FR-ARCH-001). References `Bpmn` and `Expressions` only.
- `Flowd.Application` — use cases (command handlers), ports (interfaces), transaction/locking orchestration per FR-ARCH-002. No Npgsql, no ASP.NET.
- `Flowd.Infrastructure` — Npgsql/Dapper stores, DbUp migrations, schedulers, outbox publisher, auth adapters.
- `Flowd.Api` — minimal API endpoints, middleware, mapping to/from `Flowd.Contracts`.
- `Flowd.Host` — composition root, CLI, hosting; the only project that references everything.
- `tests/Flowd.<Project>.Tests` per project, plus `Flowd.Architecture.Tests`, `Flowd.Conformance.Tests`, `Flowd.Integration.Tests` (category `Integration`).

**ARC-001** `Flowd.Architecture.Tests` (NetArchTest.Rules) MUST fail the build if: any project references outward against the arrows above; `Flowd.Engine`/`Flowd.Bpmn`/`Flowd.Expressions` use `System.Net`, `Npgsql`, `Microsoft.AspNetCore`, `System.IO.File`, `DateTime.UtcNow`/`Now`, `Guid.NewGuid` or `Random`; any non-`sealed` class lacks an `abstract`/documented reason; any `public` type exists outside an allow-listed namespace; a `switch` over a union has a discard arm (Roslyn analyzer or test).


## 3. Glossary

- **Process Definition**: a parsed, validated BPMN `<process>` deployed at a specific version.
- **Process Instance (PI)**: one running/finished execution of a definition.
- **Token / Execution**: a pointer to an element within a PI. Parallel branches = multiple tokens.
- **Scope**: container for variables and tokens (process root, sub-process, multi-instance body).
- **Wait state**: a point where execution stops until something external happens (job completion, timer, message, user task, event gateway).
- **Job**: unit of work for an external worker, created when a service task (or similar) is reached.
- **Incident**: a failure needing operator action (no retries left, unresolvable expression, etc.).
- **Command**: an intent submitted to the engine (start instance, complete job…). Processed transactionally.
- **Correlation key**: string used to route a message to a specific waiting instance.


## 4. Architecture

```
            ┌────────────────────────────────────────────────────┐
 Clients ─▶ │  API layer (REST/gRPC, authn/z, validation)        │
 Workers ─▶ │                                                    │
            └───────────────┬────────────────────────────────────┘
                            │ Commands
            ┌───────────────▼────────────────────────────────────┐
            │  Engine core (pure, deterministic, no I/O)         │
            │  - BPMN model (immutable)                          │
            │  - Interpreter: (state, command) → (state', effects)│
            │  - Expression evaluator                            │
            └───────────────┬────────────────────────────────────┘
                            │ effects (persist, outbox)
            ┌───────────────▼────────────────────────────────────┐
            │  Persistence (Postgres): definitions, instances,    │
            │  tokens, variables, jobs, timers, messages,         │
            │  incidents, history, outbox                         │
            └───────────────┬────────────────────────────────────┘
        ┌───────────────────┼───────────────────┬────────────────┐
        ▼                   ▼                   ▼                ▼
   Timer scheduler     Job timeout sweeper   Outbox publisher   Message TTL sweeper
   (all nodes, SKIP LOCKED leasing)          (webhooks / event stream)
```

### 4.1 Key architectural rules

- **FR-ARCH-001** The engine core MUST be a pure function layer: `Step(state, command, now) → (newState, effects[])`. In C#: `StepOutcome Interpreter.Step(EngineState state, EngineCommand command, DateTimeOffset now, IKeyGenerator keys)` where `EngineCommand`, `EngineEffect` and `StepOutcome` are unions (Section 2.3). No DB, network, clock or random access inside; time and IDs are injected. This enables deterministic unit tests.
- **FR-ARCH-002** Each command is processed in **one DB transaction** that locks the target process instance row (`SELECT … FOR UPDATE`), runs the interpreter until the next wait state or end, and commits all state changes + history + outbox rows atomically.
- **FR-ARCH-003** Engine nodes MUST be stateless; any node can process any command. No in-memory-only state that affects correctness.
- **FR-ARCH-004** Background schedulers MUST use DB leasing (`FOR UPDATE SKIP LOCKED`) so multiple nodes never double-process the same timer/job-timeout.
- **FR-ARCH-005** All side effects toward the outside world (webhooks, events) MUST go through the transactional **outbox** table.
- **FR-ARCH-006** The interpreter MUST bound work per command (max 10 000 element activations per command by default, configurable) to prevent infinite loops; exceeding creates an incident `LOOP_LIMIT_EXCEEDED` and stops.


## 5. Domain model

### 5.1 Entities

**ProcessDefinition**
`key (int64, unique)`, `bpmnProcessId (string)`, `version (int, per bpmnProcessId, starts 1)`, `resourceName`, `resource (xml bytes)`, `checksum (sha256)`, `deployedAt`, `tenantId`, `parsedModel (jsonb, optional cache)`.

**ProcessInstance**
`key`, `definitionKey`, `parentInstanceKey?` (call activity), `parentElementInstanceKey?`, `state` ∈ {`ACTIVE`,`COMPLETED`,`TERMINATED`,`INCIDENT`?*}, `startedAt`, `endedAt?`, `businessKey?`, `tenantId`, `version` (optimistic counter).
\* `INCIDENT` is derived (any open incident) — not stored as primary state.

**ElementInstance** (token/activity instance)
`key`, `instanceKey`, `scopeKey` (parent element instance; root scope = the process instance), `elementId`, `elementType`, `state` ∈ {`ACTIVATING`,`ACTIVE`,`COMPLETING`,`COMPLETED`,`TERMINATING`,`TERMINATED`}, `startedAt`, `endedAt?`, multi-instance fields (`loopCounter?`).

**Variable**
`scopeKey`, `name`, `value (jsonb)`, `instanceKey`. Scoped with inheritance (Section 8.3).

**Job**
`key`, `instanceKey`, `elementInstanceKey`, `type (string)`, `state` ∈ {`ACTIVATABLE`,`ACTIVATED`,`COMPLETED`,`FAILED`,`CANCELED`,`ERROR_THROWN`}, `retries`, `retryBackoffUntil?`, `deadline?`, `worker?`, `customHeaders (jsonb)`, `variablesSnapshot (jsonb)`, `errorMessage?`, `createdAt`.

**Timer**: `key`, `instanceKey?`, `elementInstanceKey?`, `dueAt`, `repetitions?`, `interval?`, `kind` ∈ {`CATCH`,`BOUNDARY`,`START`}, `state`.

**MessageSubscription**: `key`, `instanceKey`, `elementInstanceKey`, `messageName`, `correlationKey`, `kind` ∈ {`CATCH`,`BOUNDARY`,`START`,`EVENT_GATEWAY`}.

**BufferedMessage**: `key`, `name`, `correlationKey`, `variables`, `messageId?` (idempotency), `ttlUntil`, `state`.

**UserTask**: `key`, `instanceKey`, `elementInstanceKey`, `assignee?`, `candidateGroups[]`, `candidateUsers[]`, `dueDate?`, `formKey?`, `state` ∈ {`CREATED`,`COMPLETED`,`CANCELED`}.

**Incident**: `key`, `instanceKey`, `elementInstanceKey?`, `jobKey?`, `errorType` (enum), `errorMessage`, `state` ∈ {`OPEN`,`RESOLVED`}, `createdAt`.

**Key generation (FR-DOM-001):** all `key` values are 63-bit, time-ordered, globally unique snowflake-style IDs (timestamp + node id + sequence), serialized to JSON as **strings**.

### 5.2 Lifecycle state machine (element instance)

`ACTIVATING → ACTIVE → COMPLETING → COMPLETED`
`ACTIVE|ACTIVATING → TERMINATING → TERMINATED` (cancellation, interrupting boundary event, terminate end event, instance cancel).

Transitions MUST be recorded in history (Section 17) in order.


