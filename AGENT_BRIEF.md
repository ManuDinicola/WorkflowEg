# FLOWD AGENT BRIEF (single file: instructions + plan + spec)

You are a Claude agent building "Flowd", a BPMN workflow engine (Camunda-like) in a NEW repository, using C# 15 on .NET 11 (non-LTS), clean architecture, SOLID and C# 15 union types.

## What to do first (read this part only, then act)

1. This file has three parts: this header, PART 1 (PLAN, the slice table) and PART 2 (SPEC, the full requirements). Do not re-read PART 2 in full at any time. Look up requirement IDs (e.g. `FR-EXEC-004`) only when a slice needs them.
2. Enter plan mode. Confirm or adjust the M0 slices in PART 1, then ask me the five questions in PART 1 section 1 (use AskUserQuestion; recommended answers are in bold).
3. Save this file in the repo root as `AGENT_BRIEF.md`. In slice 0.1, split PART 1 into `PLAN.md` and PART 2 into `docs/spec/` files plus `docs/spec/00-index.md` (spec section PLAN-006), and create `CLAUDE.md` (under 60 lines), `DECISIONS.md`, `PROGRESS.md`. After that, agents read only the files their slice names.
4. Work one slice at a time, each on its own git branch (never commit to `main` except the single bootstrap commit), with tests written and passing before every commit. After each slice: run the gate, update `PLAN.md` and `PROGRESS.md`, push, open a PR with a test report, and stop for review as I answered.
5. Use subagents for exploration, test runs, independent slices and review, as described in spec section 0.2. Keep reports short and logs quiet to save tokens.
6. If anything is unclear, choose the simplest option consistent with the spec, record it in `DECISIONS.md`, and continue.

---

# PART 1: PLAN

# PLAN: Flowd (BPMN workflow engine, C# 15 / .NET 11)

Source of truth: `WORKFLOW_ENGINE_SPEC.md` (later `docs/spec/`). This plan follows spec Section 0.3 (PLAN-001..007).
Status legend: `[ ]` todo · `[~]` in progress · `[x]` done. Update this file and `PROGRESS.md` in each slice's last commit.

## 1. Ask the user first (PLAN-003) — recommended answers in bold

1. Slice granularity: fine (1 requirement per slice) · **medium (2–4 requirements)** · coarse (whole milestone)
2. Execution style: sequential in main session · **independent slices in parallel subagents (own worktree + branch)** · one slice per fresh session
3. Review stops: after every slice · **after every milestone**
4. Git remote: is there an `origin` URL to push to? (the agent never invents one)
5. SDK: stay on the .NET 11 RC SDK now and switch to GA when released (**yes**) or wait for GA before starting

Unattended run: take the bold answers, record them in `DECISIONS.md`, continue.

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
| [ ] 0.1 | `git init -b main`, first commit (`.gitignore`, README, LICENSE); split spec into `docs/spec/` + index; create `PLAN.md`, `CLAUDE.md`, `DECISIONS.md`, `PROGRESS.md` | GIT-001a, PLAN-006 | none (docs) | `chore/m0-bootstrap-spec-split` | S | — | main |
| [ ] 0.2 | `Flowd.slnx`, `global.json` (RC SDK, rollForward latestFeature), `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, analyzers | §2.1, CC-010, CC-011 | `dotnet build -warnaserror` green | `chore/m0-solution-scaffold` | M | 0.1 | impl-worker |
| [ ] 0.3 | Project skeleton (Section 2.4), architecture tests, union + closed-hierarchy smoke test | ARC-001, UNI-001..003 | T-BOOT-01, architecture tests | `feat/m0-project-skeleton` | M | 0.2 | impl-worker |
| [ ] 0.4 | Makefile gate targets, CI workflow, Dockerfile, docker-compose | §21.3, CI-001..004 | `make ci` green locally and in CI | `chore/m0-ci-docker` | M | 0.3 | impl-worker |
| [ ] 0.5 | DbUp runner, `flowd migrate`, `/healthz`, `/readyz` | FR-DB-001, API-013 | integration: migrate creates schema; health 200 | `feat/m0-migrations-health` | M | 0.4 | impl-worker |

### M1 — Model and deployment

| # | Slice | Requirement IDs | Tests | Branch | Size | Depends on | Executor |
|---|---|---|---|---|---|---|---|
| [ ] 1.1 | BPMN immutable model (closed hierarchy) + XXE-safe parser for MVP elements | §6.1, FR-SEC-006 | T-DEP-06, parser unit tests | `feat/m1-bpmn-model-parser` | M | 0.3 | impl-worker |
| [ ] 1.2 | Validator that collects all errors; `flowd validate` | FR-DEP-004 | T-DEP-04, T-DEP-05 | `feat/m1-bpmn-validator` | M | 1.1 | impl-worker |
| [ ] 1.3 | Definitions store, checksum versioning, deployments + definitions API | FR-DEP-001..003, FR-DEP-005, API-001, API-002 | T-DEP-01..03 | `feat/m1-deployments` | M | 1.1, 0.5 | impl-worker |

### M2 — Core execution (linear flow, gateways, jobs)

| # | Slice | Requirement IDs | Tests | Branch | Size | Depends on | Executor |
|---|---|---|---|---|---|---|---|
| [ ] 2.1 | flowd-FEEL lexer/parser/evaluator, `ExpressionValue` union, budget | §8.1, §8.2, FR-EXPR-SEC-001 | T-EXPR-01..10 | `feat/m2-expressions` | M | 0.3 | impl-worker (parallel with M1) |
| [ ] 2.2 | Engine state, key generator, `Step`, `IElementHandler<T>` registry, start/end events | FR-ARCH-001, FR-ARCH-006, FR-EXEC-001, FR-EXEC-002, FR-EXEC-008, CC-003 | T-EXEC-01 | `feat/m2-engine-core` | M | 1.1 | Plan (opus) then impl-worker |
| [ ] 2.3 | Exclusive and parallel gateways | FR-EXEC-003, FR-EXEC-004 | T-EXEC-03..07 | `feat/m2-gateways` | M | 2.1, 2.2 | impl-worker |
| [ ] 2.4 | Variables, scopes, input/output mappings | FR-VAR-001..003, §8.4 | T-EXEC-10 | `feat/m2-variables-mappings` | M | 2.1, 2.2 | impl-worker (parallel with 2.3) |
| [ ] 2.5a | Postgres schema + stores for instances, elements, variables, history | §15, FR-HIS-001 | integration | `feat/m2-stores` | M | 0.5, 2.2 | impl-worker |
| [ ] 2.5b | Command pipeline: transaction, instance row lock, lock ordering, persistence of step outcome | FR-ARCH-002, FR-ARCH-003, FR-CON-001..003 | integration (concurrency) | `feat/m2-command-pipeline` | M | 2.5a | impl-worker |
| [ ] 2.6a | Service task handler: job creation, completion, failure semantics (engine side) | FR-EXEC-006, FR-JOB-003, FR-JOB-005 | T-EXEC-02 | `feat/m2-service-task` | M | 2.2, 2.4 | impl-worker |
| [ ] 2.6b | Job store, activation (`SKIP LOCKED`, long poll), timeout sweeper, job API | FR-JOB-001..009, API-007, API-008, FR-CON-004, FR-CON-005 | T-JOB-01..08 | `feat/m2-job-api` | L→split in two at start | 2.5b, 2.6a | impl-worker |
| [ ] 2.7 | Start, cancel, query, set-variables API; history endpoint | FR-EXEC-015, API-003..006, API-012 | T-EXEC-08, T-EXEC-09 | `feat/m2-instances-api` | M | 2.5b | impl-worker |
| [ ] 2.8 | Determinism property test and loop limit | FR-EXEC-016, FR-ARCH-006 | T-EXEC-11, T-EXEC-12 | `test/m2-determinism-looplimit` | S | 2.3 | impl-worker |
| [ ] 2.9 | Milestone review: reviewer subagent vs CC-001..018, ACC-M2 run, update docs | CC-018 | ACC-M2 | `docs/m2-review` | S | all M2 | reviewer (opus) |

### M3 — Structure and control (refine at milestone start)

| # | Slice | Requirement IDs | Tests | Size | Executor |
|---|---|---|---|---|---|
| [ ] 3.1 | Embedded sub-process and scope handling | FR-EXEC-010 | T-SUB-01 | M | impl-worker |
| [ ] 3.2 | Inclusive gateway + reachability doc `docs/inclusive-join.md` | FR-EXEC-005 | T-INCL-01, T-INCL-02 | M | Plan (opus) then impl-worker |
| [ ] 3.3 | Script, manual, send tasks | FR-EXEC-007 | unit + conformance | S | impl-worker |
| [ ] 3.4 | Incidents: raise, list, resolve; incident API | FR-INC-001..003, API-010 | T-INC-01 | M | impl-worker |

### M4 — Time and messages (refine at milestone start)

| # | Slice | Requirement IDs | Tests | Size | Executor |
|---|---|---|---|---|---|
| [ ] 4.1 | Timer store, scheduler `BackgroundService`, cycle re-arming | FR-TMR-001..006 | T-TMR-04, T-TMR-05 | M | impl-worker |
| [ ] 4.2 | Timer catch and boundary events (interrupting / non-interrupting) | FR-EXEC-013 | T-TMR-01..03 | M | impl-worker |
| [ ] 4.3 | Message store, buffering, correlation, publish API | FR-MSG-001..005, API-009 | T-MSG-01..05 | M | impl-worker |
| [ ] 4.4 | Message catch/boundary/start events, receive task | FR-MSG-004 | T-MSG-04 | M | impl-worker |
| [ ] 4.5 | Event-based gateway | FR-EXEC-014 | T-EVG-01 | S | impl-worker |

### M5 — Errors, signals, user tasks (refine at milestone start)

| # | Slice | Requirement IDs | Tests | Size | Executor |
|---|---|---|---|---|---|
| [ ] 5.1 | BPMN error throw and propagation (`ErrorHandlerMatch` union) | §12.3 | T-ERR-01..03 | M | Plan (opus) then impl-worker |
| [ ] 5.2 | Signals (broadcast, catch, boundary) | FR-SIG-001 | T-SIG-01 | S | impl-worker |
| [ ] 5.3 | Intermediate throw events | §6.1 | conformance | S | impl-worker |
| [ ] 5.4 | Native user tasks + API | API-011 | T-UT-01 | M | impl-worker |

### M6 — Composition (refine at milestone start)

| # | Slice | Requirement IDs | Tests | Size | Executor |
|---|---|---|---|---|---|
| [ ] 6.1 | Call activity, variable propagation, cascade cancel | FR-EXEC-011, FR-EXEC-015 | T-CALL-01, T-CALL-02 | M | impl-worker |
| [ ] 6.2 | Multi-instance (parallel, sequential, empty collection, ordering) | FR-EXEC-012 | T-MI-01..03 | M | Plan (opus) then impl-worker |
| [ ] 6.3 | Event sub-processes | §6.1 | T-ESP-01 | M | impl-worker |

### M7 — Production readiness (refine at milestone start)

| # | Slice | Requirement IDs | Tests | Size | Executor |
|---|---|---|---|---|---|
| [ ] 7.1 | AuthN/Z (JWT, API keys, roles) and tenancy | FR-SEC-001..003 | T-SEC-01, T-SEC-02 | M | impl-worker |
| [ ] 7.2 | Outbox publisher + signed webhooks | FR-HIS-002, FR-ARCH-005 | integration | M | impl-worker |
| [ ] 7.3 | Janitor / retention | FR-DB-002 | integration | S | impl-worker |
| [ ] 7.4 | Metrics, tracing, logging polish | FR-OBS-001..003 | unit | M | impl-worker |
| [ ] 7.5 | Rate limits, idempotency keys, input limits | API-R1, FR-SEC-004 | unit + integration | M | impl-worker |
| [ ] 7.6 | `Flowd.Worker.Client` SDK (+ optional TS SDK) | §9.2 | SDK tests | M | impl-worker |
| [ ] 7.7 | BenchmarkDotNet + load scripts; union boxing check | NFR-001, NFR-002, UNI-005 | bench report | M | impl-worker |
| [ ] 7.8 | Chaos / crash recovery tests | NFR-003, NFR-005 | T-CHAOS-01 | M | impl-worker |
| [ ] 7.9 | OpenAPI contract tests, docs, runbook | API-R3, NFR-008 | contract tests | M | impl-worker |

## 4. Kickoff prompt (paste into Claude Code in the empty repo folder)

```
Read CLAUDE.md or, if it does not exist yet, WORKFLOW_ENGINE_SPEC.md sections 0, 0.1, 0.2, 0.3 and PLAN.md only.
Do not read the rest of the spec. Enter plan mode, confirm or adjust the slice table in PLAN.md for milestone M0,
ask me the five questions in PLAN.md section 1 (use AskUserQuestion), then execute slice 0.1 on its own branch.
After each slice: run the gate, update PLAN.md and PROGRESS.md, push, open a PR, and stop for review per my answer.
Use subagents for exploration, test runs and independent slices as described in spec section 0.2.
```


---

# PART 2: SPEC

# SPEC: "Flowd" — A BPMN-Based Workflow Engine (Camunda-like)

> Status: v1.0 draft · Audience: AI coding agents and human reviewers
> Working name: `flowd` (rename freely)

---

## 0. How an AI agent must use this document

1. **This file is the source of truth.** If code and spec disagree, fix the code or open a change to the spec in `DECISIONS.md`. Never silently diverge.
2. **Keywords** MUST, MUST NOT, SHOULD, MAY follow RFC 2119.
3. **Requirement IDs** (`FR-xxx`, `NFR-xxx`, `API-xxx`) are stable. Reference them in commit messages, tests and code comments (e.g. `// FR-EXEC-004`).
4. **Work order:** implement milestones in order (Section 21). Do not start milestone N+1 until every acceptance test of milestone N passes in CI.
5. **Test first for semantics.** Every BPMN element behavior in Section 6 MUST have a conformance test (Section 22) before or together with its implementation.
6. **Ambiguity protocol:** if something is unspecified, pick the simplest option consistent with this spec, record it in `DECISIONS.md` (date, question, choice, reason), and continue. Do not stall.
7. **No scope creep:** anything in "Non-goals" (Section 1.2) MUST NOT be built in v1.
8. **Definition of Done (per task):** work done on its own branch (Section 0.1), code + unit tests + conformance/integration tests green (`make ci` passes locally and in CI), `make lint` clean, docs updated, no TODOs without an issue reference.
9. **Branch-and-test is mandatory.** The agent MUST create a dedicated git branch before changing any file, and MUST write and run tests before every commit. Details in Section 0.1. Violating this section means the task is not done.
10. **Spare tokens: orchestrate, delegate, load on demand.** The agent MUST NOT read this whole file at once; it uses the spec map and delegates heavy, self-contained work (exploration, parallel implementation, test runs, review) to subagents, per Section 0.2.
11. **Plan, split, ask.** This file is a plan source. Before coding, the agent plans, splits the milestone into small slices and asks the user to approve the split (Section 0.3; the initial plan is `PLAN.md`). Stack: C# 15 on .NET 11 with clean architecture, SOLID and union types (Section 2).

### 0.1 Git workflow & testing gate (mandatory)

**Branching**
- **GIT-001** The agent MUST NOT commit directly to `main` (or `master`). Before touching any file, it creates a new branch from an up-to-date `main`:
  `git fetch origin && git checkout main && git pull --ff-only && git checkout -b <branch-name>`
- **GIT-001a New-repository bootstrap exception.** This is a brand-new repository. If `git rev-parse --verify main` fails (no commits yet), the agent runs `git init -b main`, makes **one** initial commit on `main` containing only `.gitignore` (Visual Studio/.NET template), `README.md` and `LICENSE`, and then creates `feat/m0-skeleton` from it. Every later change goes through a branch. If no `origin` remote exists, the agent stops after the first push attempt and asks the user for the remote URL; it never invents one.
- **GIT-002 Branch naming:** one branch per milestone or per task:
  - Milestone branch: `feat/m<N>-<slug>` (e.g. `feat/m2-core-execution`)
  - Task branch: `feat/<req-id>-<slug>` (e.g. `feat/fr-exec-004-parallel-gateway`), `fix/<slug>`, `test/<slug>`, `docs/<slug>`, `chore/<slug>`
  - Lowercase, hyphens only, no spaces, max 60 chars.
- **GIT-003** If the working tree is dirty or the current branch is `main` when the task starts, the agent first creates the branch (carrying nothing unrelated) and never uses `git stash`/`reset --hard` to discard the user's uncommitted work without asking.
- **GIT-004** Never force-push to a shared branch, never rewrite `main` history, never skip hooks (`--no-verify`), never delete or disable a failing test to get green.

**Commits**
- **GIT-005** Small, atomic commits using Conventional Commits and referencing requirement IDs:
  `feat(engine): implement parallel gateway join (FR-EXEC-004)`, `test(engine): add T-EXEC-06 fork/join conformance`, `fix(jobs): release lock on timeout sweep`.
- **GIT-006** Commit order within a task: failing/new test first when practical (`test:` commit), then implementation (`feat:`/`fix:`), then docs. Each commit SHOULD build and pass tests on its own.

**Testing gate (before every commit and before every push)**
- **TEST-001** The agent MUST run, in this order, and all MUST pass:
  1. `make lint` (`dotnet format --verify-no-changes` + `dotnet build -warnaserror`; includes the architecture tests of Section 2.4)
  2. `make test-unit` (`dotnet test` on all unit-test projects, no external services)
  3. `make test-conformance` (BPMN fixtures from Section 22 for the elements touched)
  4. `make test-integration` (Postgres via Testcontainers for .NET) — required whenever persistence, jobs, timers, messages or API are touched
- **TEST-002** Every new or changed requirement ID MUST have at least one test that names the ID (test name, comment or table-test label). A bug fix MUST include a regression test that fails before the fix and passes after.
- **TEST-003** If a test fails, the agent fixes the cause and re-runs the full gate. It MUST NOT mark the task complete, push, or open a PR while any test is red, flaky or skipped without a documented reason in `DECISIONS.md`.
- **TEST-004** Coverage must not drop below the thresholds in NFR-006 (engine core ≥ 90 %, overall ≥ 80 %); `make cover` fails the build otherwise.
- **TEST-005** Flaky tests are bugs: a test that fails once in 20 consecutive runs (`make test-repeat PROJECT=<test-project> N=20`) MUST be fixed, not retried. Tests MUST NOT use `Thread.Sleep`/real clocks: time comes from `FakeTimeProvider` (Section 2.3).

**Push & pull request**
- **GIT-007** After the gate is green: `git push -u origin <branch-name>`, then open a pull request against `main` (e.g. `gh pr create`) with:
  - Title in Conventional Commit style.
  - Description: what changed, requirement IDs covered, milestone, how to test.
  - A **Test report** section pasting the summary output of the four gate commands and the list of conformance test IDs (e.g. `T-EXEC-01..06`) that pass.
- **GIT-008** The agent MUST NOT merge its own PR into `main` unless the user explicitly asks. CI (Section 21.3) must be green before a merge is requested.
- **GIT-009** At the end of each milestone, tag the merge commit `v0.<N>.0` (e.g. `v0.2.0` for M2) only when the user asks for a release/tag.

**Per-task checklist the agent MUST follow (copy into the PR):**
```
[ ] Branch created from up-to-date main (GIT-001/002)
[ ] Tests written for every requirement ID touched (TEST-002)
[ ] make lint
[ ] make test-unit (with -race)
[ ] make test-conformance
[ ] make test-integration (if persistence/API/jobs/timers/messages touched)
[ ] make cover meets thresholds (TEST-004)
[ ] Docs / DECISIONS.md updated
[ ] Pushed and PR opened with Test report (GIT-007)
```

### 0.2 Claude agent operating model: token economy & subagents

This spec is executed by a **Claude agent** (e.g. Claude Code). Context and tokens are the scarce resource, so the agent works as a lean **orchestrator** that delegates heavy, self-contained work to **subagents** (the `Agent` tool). Each subagent has its own context window and returns only a short report.

**Spec map (load on demand; never read this whole file in one go)**

| Need | Read |
|---|---|
| Rules, git, test gate, token rules | Section 0 (always) |
| Scope / non-goals | Section 1 |
| Element behavior to implement | Section 6 (only the needed `FR-EXEC-xxx`) |
| Expressions & variables | Section 8 |
| Jobs / timers / messages / incidents | Sections 9, 10, 11, 12 |
| API contract | Section 14 |
| DB schema | Section 15 |
| Milestone scope & acceptance tests | Section 21 (current milestone only) + Section 22 (only the matching `T-…` IDs) |

**Rules**

- **TOK-001 Lean orchestrator.** The main agent does not read the full spec. It finds what it needs with `Grep` on requirement IDs (e.g. `FR-EXEC-004`, `T-JOB-0`) and `Read` with `offset`/`limit`. At the start of a milestone it reads only: Section 0, the milestone entry in 21.2, and the requirement IDs that milestone lists.
- **TOK-002 When to delegate (spawn a subagent).**
  - Exploring or searching the repo across more than ~3 files → `Explore` subagent (read-only, returns the conclusion, not file dumps).
  - Implementing one self-contained task (one requirement ID or one package) → `general-purpose` (or custom) subagent.
  - Writing a batch of conformance fixtures/tests (Section 22) → test-writer subagent.
  - Running the test gate and triaging failures → test-runner subagent (returns only a summary).
  - Independent review of a finished diff against its requirement IDs → reviewer subagent with fresh context.
  - Designing an approach for a non-trivial task (e.g. inclusive-join reachability, FR-EXEC-005) → `Plan` subagent.
- **TOK-003 When NOT to delegate.** Tiny edits (< ~30 lines), one-file fixes, decisions that need the whole conversation, and anything where writing the brief costs more than doing the work.
- **TOK-004 Subagents start cold.** Every brief MUST be self-contained and use this template:
  ```
  GOAL: <one sentence>
  REQUIREMENTS: <IDs, and the spec section numbers to read — do not read the whole spec>
  BRANCH: <branch name or worktree to use>
  FILES/PACKAGES: <where to work, what not to touch>
  CONSTRAINTS: pure engine core (FR-ARCH-001), tests required (TEST-002), no scope creep
  GATE: <exact make targets to run before finishing>
  REPORT (max 150 words): files changed · tests added (IDs) · gate result · open issues/decisions
  ```
- **TOK-005 Parallelism.** Spawn several subagents in one turn only for tasks with **disjoint files/packages** (e.g. `src/Flowd.Bpmn` parser vs `src/Flowd.Expressions` evaluator). Max 3–4 concurrent. Each parallel subagent works in its own git worktree/branch (`isolation: "worktree"`, branch per GIT-002). Never let two subagents edit the same file. The orchestrator merges/rebases sequentially and resolves conflicts.
- **TOK-006 Trust but verify.** A subagent's report is not proof. The orchestrator MUST run `make ci` on the integrated branch before pushing or declaring a task done (TEST-003 still applies).
- **TOK-007 Quiet output discipline.** Never stream full test/build logs into context. Redirect and show only the tail or failures:
  `make test-unit > /tmp/unit.log 2>&1; echo "exit=$?"; grep -E "^(--- FAIL|FAIL|panic)" /tmp/unit.log | head -30`
  Use `-run <pattern>` to re-run only the failing test while fixing, then the full gate once at the end.
- **TOK-008 No redundant reads.** Do not re-read a file just edited or just written. Prefer `Grep`/`Glob` over reading whole files. Refer to code as `path:line` in reports instead of pasting it. Prefer `Edit` (diff) over rewriting whole files.
- **TOK-009 Checkpoint file.** Maintain `PROGRESS.md` (≤ 60 lines): current milestone and branch, requirement IDs done, next task, blockers, decisions pointer. Update it in every task's last commit. A fresh session or subagent resumes from `PROGRESS.md` instead of replaying history.
- **TOK-010 Model tiering for subagents** (use the `model` override when available):
  - `haiku` — read-only exploration, running tests, summarizing logs, formatting/docs chores.
  - `sonnet` — default for implementation and writing tests.
  - `opus` — architecture/planning of hard parts (inclusive join, multi-instance, concurrency/locking in Section 16) and final review of a milestone.
- **TOK-011 Stop conditions.** If a subagent returns the same failure twice, the orchestrator stops delegating that task, reads the failing area itself, and either fixes it or records the blocker in `DECISIONS.md`/`PROGRESS.md` and moves to the next task.

**Suggested custom subagents** (optional, define in `.claude/agents/*.md`; keep each prompt < 40 lines):

| Name | Model | Tools | Job |
|---|---|---|---|
| `spec-reader` | haiku | Grep, Read | Given requirement IDs, returns the exact requirement text and linked tests in ≤ 200 words |
| `impl-worker` | sonnet | all | Implements one requirement-scoped task on a given branch/worktree, runs its gate, reports per TOK-004 |
| `bpmn-test-writer` | sonnet | Read, Write, Edit, Bash | Creates `testdata/bpmn/*.bpmn` and the matching `T-…` tests from Section 22 |
| `test-runner` | haiku | Bash, Grep | Runs the gate, returns pass/fail, failing test names, first error line each |
| `reviewer` | opus | Read, Grep, Bash (read-only) | Reviews a diff against requirement IDs, determinism (FR-EXEC-016), locking (Section 16); returns a ≤ 200-word findings list |

**Example delegation plan for M2 (illustrative)**
1. Orchestrator: create `feat/m2-core-execution`; read FR-EXEC-001..010, FR-ARCH-001..006 only.
2. In parallel (worktrees): `impl-worker` → `src/Flowd.Expressions` (Section 8.2); `impl-worker` → `src/Flowd.Infrastructure` Postgres jobs store (Section 15 + FR-JOB-001..004); `bpmn-test-writer` → T-EXEC-01..12 fixtures.
3. Sequential: `impl-worker` → interpreter step handlers (depends on the above); orchestrator merges.
4. `test-runner` → full gate; orchestrator fixes or re-delegates failures (TOK-011).
5. `reviewer` → diff vs FR-EXEC/FR-JOB IDs; orchestrator applies findings, updates `PROGRESS.md`, pushes, opens PR (GIT-007).

**Repo-level `CLAUDE.md` (create in M0; keep under 60 lines so it is cheap to load every session)** SHOULD contain: project one-liner, the Makefile gate targets, the branch/commit rules (GIT-001..006), the TOK rules in one-line form, the Spec map table above, the C# conventions of Section 2.2 in one-line form, and a pointer to `PROGRESS.md`.

### 0.3 Planning mode: split the work first, then ask (token economy)

This spec is a **plan source**, not a single task. The agent MUST NOT try to build the engine in one session or one giant change. Before writing code it plans, **splits** the work into small slices, and **asks the user to confirm the split**.

- **PLAN-001 Plan first.** At the start of the project and again at the start of every milestone, the agent enters plan mode (`EnterPlanMode`) or produces an equivalent written plan, reading only the spec-map sections it needs (TOK-001). No code is written before the plan exists.
- **PLAN-002 Split into slices.** The plan breaks the milestone into **slices**. A slice is the smallest unit that can be built, tested and reviewed alone:
  - one requirement ID or one tight cluster (e.g. `FR-EXEC-003..004`, or "parser for service/script tasks");
  - touches at most ~3 projects and about 400 changed lines;
  - ends with a green gate (TEST-001) and one PR (GIT-007);
  - has its own branch (GIT-002), its own acceptance tests (`T-…` IDs) and an estimate of context cost (S / M / L).
  A slice marked L MUST be split again.
- **PLAN-003 Ask before starting.** The agent MUST ask the user to approve the split (use `AskUserQuestion` when available, otherwise one plain question) before executing it. The question offers concrete choices: granularity (fine: 1 requirement per slice · medium: 2–4 · coarse: whole milestone), execution style (one slice per fresh session · subagents in parallel for independent slices · sequential in the main session), and whether to stop for review after each slice or after each milestone. It recommends one option and proceeds with it if the user does not answer in unattended runs, recording the choice in `DECISIONS.md`.
- **PLAN-004 Plan output format** (written to `PLAN.md`, ≤ 80 lines, one table):
  `| # | Slice | Requirement IDs | Tests (T-…) | Branch | Size | Depends on | Executor (main / subagent / own session) |`
- **PLAN-005 One slice, one context.** Each slice is executed in a **fresh subagent or a fresh session** that receives only its row from `PLAN.md`, the spec excerpts it names, and `PROGRESS.md` — never the whole spec or the whole conversation (TOK-004 brief template).
- **PLAN-006 Split the spec itself (first task of M0).** The first slice of M0 is a chore: split `SPEC.md` into `docs/spec/NN-<topic>.md` files, one per top-level section (rules, architecture, BPMN semantics, expressions, jobs/timers/messages, API, schema, security/observability, milestones, tests), plus a ≤ 60-line `docs/spec/00-index.md` that holds the spec map. Requirement IDs and numbering MUST NOT change. After the split, `CLAUDE.md` points to the index and agents read only the file(s) their slice names.
- **PLAN-007 Re-plan on drift.** If a slice grows beyond its size estimate, the agent stops, splits the remainder into new slices, updates `PLAN.md`, and tells the user in one short message instead of pushing on.

---

## 1. Overview

### 1.1 Goals

Flowd is a **process orchestration engine**: it deploys BPMN 2.0 process definitions, starts and runs process instances durably, coordinates external **job workers** and human tasks, handles timers/messages/errors, and exposes everything through an API, with full audit history.

Core capabilities (v1):
- G1. Deploy BPMN 2.0 XML (and optionally DMN 1.3 later) with versioning.
- G2. Execute process instances with correct BPMN token semantics.
- G3. Durable state; survive crashes and restarts without losing or duplicating work (at-least-once job delivery, exactly-once state transitions).
- G4. External-worker model (long-polling job activation), like Camunda 8 / Zeebe and Camunda 7 "External Tasks".
- G5. Timers, message correlation, signals, boundary events, error handling, incidents.
- G6. REST API (+ optional gRPC), OpenAPI documented.
- G7. Horizontally scalable stateless API/engine nodes on top of PostgreSQL.
- G8. Observability: metrics, structured logs, traces, audit history.

### 1.2 Non-goals (v1)

- Graphical modeler (use bpmn.io externally). A read-only instance viewer MAY come later.
- Full FEEL or full DMN compliance (a defined subset only, Section 8).
- In-process Java-style delegates / arbitrary code execution inside the engine. **No script engines that execute untrusted code.**
- CMMN, case management, BPMN choreography/conversation diagrams.
- Event-sourced distributed log à la Zeebe (we use a relational DB; see Section 4).
- Compensation, transactions sub-processes, ad-hoc sub-processes (planned post-v1).
- Multi-region active-active.

### 1.3 Compatibility stance

Flowd implements a **documented subset of BPMN 2.0.2** and a Zeebe-flavored extension namespace for engine-specific attributes (`xmlns:flowd="http://flowd.io/schema/1.0"`). Models authored in bpmn.io/Camunda Modeler that use only supported elements MUST import cleanly. Unsupported elements MUST be rejected at deployment with a precise error (element id, type, reason), never silently ignored.

---

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

---

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

---

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

---

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

---

## 6. BPMN support matrix & execution semantics

Legend: ✅ v1 · 🔜 milestone noted · ❌ not supported (reject at deploy)

### 6.1 Elements

| Element | Variants | Status |
|---|---|---|
| Process | executable only (`isExecutable=true`) | ✅ |
| Start Event | none, message, timer, signal | ✅ (message/timer M4, signal M5) |
| End Event | none, terminate, error, message(→job) | ✅ |
| Service Task | job-worker (`flowd:taskDefinition`) | ✅ |
| Send Task | as service task | ✅ M3 |
| Receive Task | message | ✅ M4 |
| User Task | job-less native user task | ✅ M5 |
| Script Task | expression-only (`flowd:script` = expression) | ✅ M3 |
| Business Rule Task | DMN decision-table (call decision) | 🔜 post-v1 (or worker-based in v1) |
| Manual Task | pass-through | ✅ |
| Exclusive Gateway | with conditions + default flow | ✅ |
| Parallel Gateway | fork/join | ✅ |
| Inclusive Gateway | fork/join | ✅ M3 |
| Event-Based Gateway | timer/message catch targets | ✅ M4 |
| Intermediate Catch | timer, message, signal | ✅ M4 |
| Intermediate Throw | none, message(→job), signal | ✅ M5 |
| Boundary Event | timer, message, error, signal; interrupting & non-interrupting | ✅ M4/M5 |
| Embedded Sub-Process | | ✅ M3 |
| Event Sub-Process | message/timer/error triggered | 🔜 M6 |
| Call Activity | by `calledElement` (latest or pinned version), variable mapping | ✅ M6 |
| Multi-Instance | parallel & sequential, over collection | ✅ M6 |
| Compensation, Transaction, Ad-hoc SP, Complex Gateway | | ❌ |

### 6.2 Execution semantics (normative)

**FR-EXEC-001 Start.** Starting an instance creates a PI, a root scope, applies provided variables to the root scope, then activates the (single, none-type) start event — or the specified start event if the definition has multiple and `startElementId` is given.

**FR-EXEC-002 Sequence flow.** When an activity completes, a token is created on **every** outgoing sequence flow of non-gateway elements (implicit parallel split). Conditions on outgoing flows of non-gateway elements MUST be rejected at deploy time unless the model is a *conditional flow* (not supported v1).

**FR-EXEC-003 Exclusive gateway.** Evaluate outgoing flow conditions **in document order**; take the first that evaluates to `true`. If none and a default flow exists, take default. If none: raise incident `CONDITION_ERROR` ("no outgoing flow matched") at the gateway. A condition evaluating to a non-boolean is an incident `CONDITION_ERROR`. Diverging with one outgoing flow and no condition is allowed (pass-through).

**FR-EXEC-004 Parallel gateway.**
- Diverging: emit one token per outgoing flow. Conditions are ignored/rejected at deploy.
- Converging (join): wait until a token has arrived on **each** incoming flow, then consume exactly one token per incoming flow and emit one token per outgoing flow. Extra tokens on a flow remain queued for the next join cycle.

**FR-EXEC-005 Inclusive gateway.**
- Diverging: evaluate all conditions; emit tokens for all that are `true`; if none, use default; if still none → incident.
- Converging: fire when no other token can still reach this gateway via a path from any *other* active token (reachability analysis over the static graph restricted to the current scope). Document the algorithm in `docs/inclusive-join.md`.

**FR-EXEC-006 Service task.** On activation: evaluate `flowd:taskDefinition type` and `retries` (static or expression), evaluate input mappings (Section 8.4) into the task-local scope, create a Job in `ACTIVATABLE`. The token waits. On job completion: apply output mappings, complete the activity, continue.

**FR-EXEC-007 Script task.** Evaluate `flowd:script expression` with variables in scope, store result in `resultVariable`. Failure → incident `EXTRACT_VALUE_ERROR`.

**FR-EXEC-008 End events.**
- None: consume token; the scope completes when it has no remaining tokens/active children. Root scope completion ⇒ PI `COMPLETED`.
- Terminate: terminate **all** active element instances in the enclosing scope, then complete the scope.
- Error end event: throw BPMN error with code (Section 12.3).

**FR-EXEC-009 Scope completion.** A scope (process, sub-process) completes iff it has zero active element instances and zero tokens waiting at joins that can never fire. Unmatched join tokens at completion ⇒ incident `UNCOMPLETABLE_JOIN`? **Decision:** log a warning event and complete the scope anyway (matches Zeebe behavior); record in `DECISIONS.md`.

**FR-EXEC-010 Sub-process.** Activation creates a child scope; its start event is activated. Variables: child scope inherits parent variables (read), writes go to the scope where the variable exists, else to the *local* scope unless output mapping targets propagation (see 8.3).

**FR-EXEC-011 Call activity.** Activation starts a new PI (child) of the called process (`calledElement` = `bpmnProcessId`; `flowd:propagateAllChildVariables` default `true`; `propagateAllParentVariables` default `true`). Child completion completes the call activity; child termination/failure with unhandled error propagates the BPMN error to the parent element. Cancelling the parent cancels children (cascade).

**FR-EXEC-012 Multi-instance.**
- `flowd:loopCharacteristics` `inputCollection` (expression → array), `inputElement` (variable name per iteration), `outputElement` (expression per iteration), `outputCollection` (variable name).
- Parallel: create all body instances at once; sequential: one after another.
- Output collection preserves input order regardless of completion order.
- Empty input collection ⇒ activity completes immediately with an empty output collection.
- Completion condition (`completionCondition`) MAY be supported in M6; if present and true after an iteration, remaining instances are terminated.

**FR-EXEC-013 Boundary events.**
- Attached to an activity; the boundary subscription/timer is created **when the activity is activated** and removed when it completes or terminates.
- Interrupting: when triggered, terminate the attached activity (and its subtree, jobs, subscriptions), then continue from the boundary event's outgoing flows.
- Non-interrupting: spawn a new token from the boundary event while the activity continues. Timer cycle (`R3/PT10M`) allowed on non-interrupting only.

**FR-EXEC-014 Event-based gateway.** On activation create all subscriptions/timers for its target catch events; the **first** to trigger wins; all other subscriptions/timers are removed; the token continues from the winning event.

**FR-EXEC-015 Cancel.** `CancelInstance` terminates all element instances depth-first (leaves first), cancels jobs (`CANCELED`), removes timers/subscriptions, cascades to child instances, and sets PI `TERMINATED`.

**FR-EXEC-016 Determinism.** Given identical (state, command, now, id sequence), the interpreter MUST produce identical output. Property-based tests SHOULD assert this.

---

## 7. Interpreter algorithm (reference)

```
processCommand(cmd):
  tx.begin
  inst = lockInstance(cmd.instanceKey)            # SELECT FOR UPDATE
  state = load(inst)                              # elements, variables, subs, timers
  queue = [initialStepFrom(cmd)]
  while queue not empty and steps < LIMIT:
      step = queue.pop()
      effects = interpreter.apply(state, step)    # pure
      state.mutate(effects.stateChanges)
      queue.push(effects.followUpSteps)           # e.g. ACTIVATE next element
      outbox.add(effects.externalEvents)
      history.add(effects.historyRecords)
  persist(state diff)
  tx.commit
```

- **FR-INT-001** Steps are processed FIFO per command (breadth-first) to give predictable ordering of parallel branches; ordering within a command MUST be stable and documented.
- **FR-INT-002** Incident creation inside a step MUST NOT roll back the transaction; it records the incident, leaves the token in its current state, and stops that branch only.
- **FR-INT-003** Resolving an incident (`POST /incidents/{key}/resolve`) re-queues the failed step.

---

## 8. Expression language

### 8.1 Choice
**FR-EXPR-001** Implement a **FEEL subset** ("flowd-FEEL") behind the interface `Evaluator.Eval(expr string, ctx Context) (Value, error)`. If a maintained FEEL library exists for the chosen language, wrap it; otherwise implement the subset below. Static expressions are plain literals; dynamic expressions start with `=` (e.g., `= order.total > 100`), as in Zeebe.

### 8.2 Supported subset (MUST)
- Literals: numbers (int/decimal), strings `"…"`, booleans, `null`, lists `[1,2]`, contexts `{a:1}`.
- Paths: `a.b.c`, `a[1]` (1-based), `a[item > 2]` (filter), `a.b[1].c`.
- Operators: `+ - * / ** `, `= != < <= > >=`, `and or not()`, `in`, `between … and …`, string `+`.
- `if … then … else …`.
- Built-ins (minimum): `count, sum, min, max, append, contains, starts with, ends with, upper case, lower case, substring, string length, number, string, is defined, date and time, now(), duration, abs, floor, ceiling, round`.
- ISO-8601 durations/dates in timers: `PT15M`, `P1D`, `R3/PT10M`, dates `2026-12-01T10:00:00Z`.

### 8.3 Variable scoping
- **FR-VAR-001** Lookup walks from the current scope up to the root. Write semantics: **set-in-defining-scope** (if variable exists in an ancestor scope, update there; else create in the scope where it's set).
- **FR-VAR-002** Variable values MUST be valid JSON. Max size per variable 1 MiB; max total per instance 16 MiB (configurable) → violation raises incident `VARIABLE_SIZE_EXCEEDED`.
- **FR-VAR-003** Reserved names: prefix `_flowd` (reserved).

### 8.4 Input/output mappings
- `<flowd:ioMapping><flowd:input source="= expr" target="name"/><flowd:output source="= expr" target="name"/></flowd:ioMapping>`.
- Inputs create **local** variables in the activity scope; outputs are evaluated against (job result variables ∪ local scope) and written to the **parent** scope.
- Without any output mapping, **all** result variables are propagated to the parent scope (Camunda 8 default; configurable via `flowd:propagateAll=false`).

### 8.5 Security
- **FR-EXPR-SEC-001** Evaluator MUST be side-effect free, non-Turing-complete (no unbounded loops/recursion), with an evaluation budget (max 100 000 steps, 100 ms wall time default).

---

## 9. Jobs & worker protocol

Workers are external processes. They pull jobs over HTTP long-poll (gRPC streaming MAY follow).

### 9.1 Semantics
- **FR-JOB-001 Activation.** `ActivateJobs(type, worker, timeoutMs, maxJobs, fetchVariables[], requestTimeoutMs)` atomically moves up to `maxJobs` jobs of `type` from `ACTIVATABLE` to `ACTIVATED`, sets `deadline = now + timeoutMs`, `worker`, decrements nothing. Uses `SELECT … FOR UPDATE SKIP LOCKED`. Long-poll up to `requestTimeoutMs` (default 10 s, max 60 s) using Postgres `LISTEN/NOTIFY` (fallback: poll every 100–500 ms).
- **FR-JOB-002 Completion.** `CompleteJob(jobKey, variables)` is valid only for `ACTIVATED` jobs; applies output mapping, advances the process instance. Idempotency: completing an already `COMPLETED` job returns `409 JOB_NOT_ACTIVATED` with state info; workers must treat as success if their own prior attempt succeeded.
- **FR-JOB-003 Failure.** `FailJob(jobKey, retries, errorMessage, retryBackoffMs?)` sets remaining retries. If `retries == 0` → create incident `JOB_NO_RETRIES`, job state `FAILED`. Otherwise back to `ACTIVATABLE` after `retryBackoffMs`.
- **FR-JOB-004 Timeout.** A sweeper moves jobs with `deadline < now` back to `ACTIVATABLE` (retries **not** decremented). At-least-once delivery ⇒ workers MUST be idempotent. Document this prominently.
- **FR-JOB-005 BPMN error.** `ThrowError(jobKey, errorCode, errorMessage, variables?)` triggers error handling (Section 12.3). Job state → `ERROR_THROWN`.
- **FR-JOB-006 Update.** `UpdateRetries`, `UpdateTimeout` endpoints.
- **FR-JOB-007 Fairness.** Within a job type, activation order is by `createdAt` ascending (FIFO), best-effort.
- **FR-JOB-008 Fetch variables.** Only the requested variables (or all if unspecified) are returned to the worker, evaluated from the activity scope after input mappings.
- **FR-JOB-009 Custom headers.** Static `flowd:taskHeaders` are returned to workers with the job.

### 9.2 Reference worker SDK
Provide a minimal C# SDK `Flowd.Worker.Client` (NuGet-packable, references only `Flowd.Contracts`) with: `FlowdClient.CreateWorker(type, handler, options)`, concurrency and timeout options, automatic retry with jittered backoff, graceful shutdown via `CancellationToken`/`IHostedService` integration, `job.CompleteAsync(vars)`, `job.FailAsync(msg, retries)`, `job.ThrowErrorAsync(code, msg)`; handler results modeled as a union (`JobResult`: `Completed`, `Failed`, `BpmnError`). A TypeScript SDK `sdk/ts` is optional. (SDKs are M7; the engine MUST NOT depend on them.)

---

## 10. Timers

- **FR-TMR-001** Timer definitions: `timeDate` (absolute), `timeDuration` (relative to activation), `timeCycle` (`R[n]/<duration>` ISO repeating). Expressions allowed (`= …`).
- **FR-TMR-002** Scheduler loop per node: every 250 ms (configurable) `SELECT … FROM timers WHERE due_at <= now() AND state='ACTIVE' ORDER BY due_at FOR UPDATE SKIP LOCKED LIMIT N`, then process each as a command (`TriggerTimer`) in its own transaction.
- **FR-TMR-003** Timers MUST survive restarts; overdue timers fire on startup, in `dueAt` order.
- **FR-TMR-004** Time source injected; tests use a fake clock and `AdvanceTime(d)` helper in the test engine.
- **FR-TMR-005** Timer start events: on deployment of version N, create the timer; remove timers of version N-1 for the same process id.
- **FR-TMR-006** Cycle timers re-arm after each firing, with drift-free scheduling (`next = previousDue + interval`).

---

## 11. Messages & signals

- **FR-MSG-001 Publish.** `PublishMessage(name, correlationKey, variables, ttlMs, messageId?)`.
- **FR-MSG-002 Correlation.** A message correlates to **every distinct process definition** subscription (at most **one** instance per definition) whose `(name, correlationKey)` match. If no subscription matches now and `ttlMs > 0`, the message is buffered until TTL expiry and correlated as soon as a matching subscription opens.
- **FR-MSG-003 Idempotency.** A non-empty `messageId` MUST reject duplicates of the same `(name, correlationKey, messageId)` while the message is still buffered/not expired (`409 MESSAGE_ALREADY_EXISTS`).
- **FR-MSG-004 Message start events.** A published message that matches a message start event creates a new instance (once per buffered message; uniqueness per `(name, correlationKey)` while an instance created by that correlation is active — configurable).
- **FR-MSG-005 Correlation key** is evaluated (expression) when the subscribing element is activated; non-string results → incident `EXTRACT_VALUE_ERROR`.
- **FR-SIG-001 Signals.** `BroadcastSignal(name, variables)` triggers **all** matching signal subscriptions across instances; never buffered.

---

## 12. Errors, retries, incidents

### 12.1 Incident types (enum)
`JOB_NO_RETRIES`, `CONDITION_ERROR`, `EXTRACT_VALUE_ERROR`, `IO_MAPPING_ERROR`, `CALLED_ELEMENT_ERROR`, `UNHANDLED_ERROR_EVENT`, `VARIABLE_SIZE_EXCEEDED`, `LOOP_LIMIT_EXCEEDED`, `MESSAGE_SIZE_EXCEEDED`, `UNKNOWN`.

### 12.2 Rules
- **FR-INC-001** An open incident blocks only the affected token; other branches continue.
- **FR-INC-002** `ResolveIncident` after the operator fixes the cause (updated variables, job retries) re-executes the failed step; for `JOB_NO_RETRIES` the operator first calls `UpdateRetries(jobKey, n>0)`, then resolves.
- **FR-INC-003** Incidents MUST be listed/filterable via API and emit outbox events `INCIDENT_CREATED/RESOLVED`.

### 12.3 BPMN error propagation
When an error with `errorCode` is thrown (job `ThrowError` or error end event):
1. Search for a matching **error boundary event** on the throwing activity (match `errorRef.errorCode`, or catch-all if none specified), then up the scope chain (enclosing sub-process boundary events, event sub-processes M6, then call-activity parent).
2. First match wins (innermost). The matched activity is interrupted (terminated) and flow continues from the boundary.
3. No match ⇒ incident `UNHANDLED_ERROR_EVENT` at the throwing element.

---

## 13. Deployment & versioning

- **FR-DEP-001** `POST /v1/deployments` accepts multipart `.bpmn` (and later `.dmn`, `.form`) files. Parse → validate → persist atomically; all-or-nothing per deployment.
- **FR-DEP-002 Versioning.** If the XML checksum for a `bpmnProcessId` equals the latest version's checksum, return the existing definition (no new version, idempotent). Otherwise assign `version = latest + 1`.
- **FR-DEP-003** New instances use **latest** version unless `version` is specified. Running instances keep their definition forever.
- **FR-DEP-004 Validation (reject with 400 and list of all errors):** well-formed XML; XSD valid; each process has exactly one process id; unique element ids; all `sourceRef/targetRef` resolve; start event exists (≥1); no unsupported elements; service tasks have `taskDefinition type`; gateways with conditions on all non-default outgoing flows (exclusive/inclusive diverging); boundary events attached to supported activities; expressions parse; timer definitions parse; call activities' `calledElement` is a non-empty string (existence checked at runtime).
- **FR-DEP-005** `GET /v1/process-definitions/{key}/xml` returns the original unmodified XML.
- **FR-DEP-006** Process definition deletion is NOT supported in v1 (only `DELETE` of definitions with zero instances MAY be added behind a flag).

---

## 14. API

Base path `/v1`. JSON; all keys as strings; timestamps RFC 3339 UTC; errors per RFC 9457 (`application/problem+json`) with `type`, `title`, `status`, `detail`, `code` (stable machine code).

### 14.1 Endpoints (REST)

| ID | Method & path | Purpose |
|---|---|---|
| API-001 | `POST /deployments` | Deploy resources |
| API-002 | `GET /process-definitions` · `GET /process-definitions/{key}` · `/xml` | Query definitions |
| API-003 | `POST /process-instances` `{bpmnProcessId, version?, variables?, businessKey?, startElementId?}` | Start instance; returns `{processInstanceKey,…}` |
| API-004 | `GET /process-instances/{key}` · `GET /process-instances?filter…` | Query |
| API-005 | `POST /process-instances/{key}/cancel` | Cancel |
| API-006 | `PUT /process-instances/{key}/variables` `{variables, scopeKey?, local?}` | Set variables |
| API-007 | `POST /jobs/activate` | Long-poll job activation (Section 9) |
| API-008 | `POST /jobs/{key}/complete` · `/fail` · `/error` · `PATCH /jobs/{key}` (retries/timeout) | Job lifecycle |
| API-009 | `POST /messages` · `POST /signals` | Publish message/broadcast signal |
| API-010 | `GET /incidents` · `POST /incidents/{key}/resolve` | Incident ops |
| API-011 | `GET /user-tasks` · `POST /user-tasks/{key}/claim|unclaim|complete|assign` | User tasks (M5) |
| API-012 | `GET /process-instances/{key}/history` | Ordered element-instance & event history |
| API-013 | `GET /healthz` (liveness) · `GET /readyz` (DB reachable, migrations current) · `GET /metrics` (Prometheus) | Ops |

### 14.2 API rules
- **API-R1** Every mutating endpoint accepts optional header `Idempotency-Key`; same key + same body within 24 h returns the original response.
- **API-R2** List endpoints use cursor pagination (`pageSize` ≤ 1000, `after` cursor, response `{items, nextCursor}`) and filters (state, bpmnProcessId, time ranges, tenant, business key, variable equals — variable filter MAY be M7).
- **API-R3** `openapi.yaml` is generated/maintained alongside code; CI validates that handlers match the spec (contract tests).
- **API-R4** Standard error codes (minimum): `NOT_FOUND`, `INVALID_ARGUMENT`, `DEPLOYMENT_INVALID`, `INSTANCE_NOT_ACTIVE`, `JOB_NOT_ACTIVATED`, `MESSAGE_ALREADY_EXISTS`, `UNAUTHENTICATED`, `PERMISSION_DENIED`, `RESOURCE_EXHAUSTED`, `INTERNAL`.

### 14.3 Example: start instance

```http
POST /v1/process-instances
Content-Type: application/json

{ "bpmnProcessId": "order-process", "variables": { "order": { "id": "A-17", "total": 120.5 } } }
```
```json
{ "processInstanceKey": "2251799813685249", "processDefinitionKey": "2251799813685200", "version": 3, "bpmnProcessId": "order-process" }
```

### 14.4 Example: worker loop

```http
POST /v1/jobs/activate
{ "type": "charge-card", "worker": "billing-1", "timeoutMs": 30000, "maxJobs": 10, "requestTimeoutMs": 20000, "fetchVariables": ["order"] }
```
→ `{ "jobs": [ { "key":"…", "type":"charge-card", "processInstanceKey":"…", "elementId":"Task_Charge", "retries":3, "deadline":"…", "variables":{"order":{…}}, "customHeaders":{} } ] }`

---

## 15. Persistence schema (PostgreSQL, normative core)

Use these tables (names fixed; add indexes as stated). All `key` columns are `BIGINT`.

```sql
CREATE TABLE process_definition (
  key BIGINT PRIMARY KEY, bpmn_process_id TEXT NOT NULL, version INT NOT NULL,
  resource_name TEXT NOT NULL, resource BYTEA NOT NULL, checksum BYTEA NOT NULL,
  tenant_id TEXT NOT NULL DEFAULT '', deployed_at TIMESTAMPTZ NOT NULL,
  UNIQUE (tenant_id, bpmn_process_id, version)
);
CREATE INDEX ON process_definition (tenant_id, bpmn_process_id, version DESC);

CREATE TABLE process_instance (
  key BIGINT PRIMARY KEY, definition_key BIGINT NOT NULL REFERENCES process_definition(key),
  parent_instance_key BIGINT, parent_element_instance_key BIGINT,
  state TEXT NOT NULL CHECK (state IN ('ACTIVE','COMPLETED','TERMINATED')),
  business_key TEXT, tenant_id TEXT NOT NULL DEFAULT '',
  started_at TIMESTAMPTZ NOT NULL, ended_at TIMESTAMPTZ, row_version BIGINT NOT NULL DEFAULT 0
);
CREATE INDEX ON process_instance (state, started_at);
CREATE INDEX ON process_instance (business_key) WHERE business_key IS NOT NULL;
CREATE INDEX ON process_instance (parent_instance_key) WHERE parent_instance_key IS NOT NULL;

CREATE TABLE element_instance (
  key BIGINT PRIMARY KEY, instance_key BIGINT NOT NULL REFERENCES process_instance(key),
  scope_key BIGINT NOT NULL, element_id TEXT NOT NULL, element_type TEXT NOT NULL,
  state TEXT NOT NULL, loop_counter INT, started_at TIMESTAMPTZ NOT NULL, ended_at TIMESTAMPTZ
);
CREATE INDEX ON element_instance (instance_key, state);
CREATE INDEX ON element_instance (scope_key);

CREATE TABLE variable (
  instance_key BIGINT NOT NULL, scope_key BIGINT NOT NULL, name TEXT NOT NULL,
  value JSONB NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
  PRIMARY KEY (scope_key, name)
);
CREATE INDEX ON variable (instance_key);

CREATE TABLE job (
  key BIGINT PRIMARY KEY, instance_key BIGINT NOT NULL, element_instance_key BIGINT NOT NULL,
  type TEXT NOT NULL, state TEXT NOT NULL, retries INT NOT NULL,
  retry_backoff_until TIMESTAMPTZ, deadline TIMESTAMPTZ, worker TEXT,
  custom_headers JSONB NOT NULL DEFAULT '{}', variables JSONB NOT NULL DEFAULT '{}',
  error_message TEXT, tenant_id TEXT NOT NULL DEFAULT '', created_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX job_activatable ON job (tenant_id, type, created_at) WHERE state = 'ACTIVATABLE';
CREATE INDEX job_deadline ON job (deadline) WHERE state = 'ACTIVATED';

CREATE TABLE timer (
  key BIGINT PRIMARY KEY, instance_key BIGINT, element_instance_key BIGINT,
  definition_key BIGINT, element_id TEXT NOT NULL, kind TEXT NOT NULL,
  due_at TIMESTAMPTZ NOT NULL, repetitions INT, interval_iso TEXT, state TEXT NOT NULL DEFAULT 'ACTIVE'
);
CREATE INDEX timer_due ON timer (due_at) WHERE state = 'ACTIVE';

CREATE TABLE message_subscription (
  key BIGINT PRIMARY KEY, instance_key BIGINT, element_instance_key BIGINT,
  definition_key BIGINT, element_id TEXT NOT NULL, kind TEXT NOT NULL,
  message_name TEXT NOT NULL, correlation_key TEXT NOT NULL, tenant_id TEXT NOT NULL DEFAULT ''
);
CREATE INDEX ON message_subscription (tenant_id, message_name, correlation_key);

CREATE TABLE buffered_message (
  key BIGINT PRIMARY KEY, name TEXT NOT NULL, correlation_key TEXT NOT NULL,
  message_id TEXT, variables JSONB NOT NULL DEFAULT '{}', ttl_until TIMESTAMPTZ NOT NULL,
  tenant_id TEXT NOT NULL DEFAULT '', created_at TIMESTAMPTZ NOT NULL
);
CREATE UNIQUE INDEX ON buffered_message (tenant_id, name, correlation_key, message_id) WHERE message_id IS NOT NULL;
CREATE INDEX ON buffered_message (ttl_until);

CREATE TABLE incident (
  key BIGINT PRIMARY KEY, instance_key BIGINT NOT NULL, element_instance_key BIGINT, job_key BIGINT,
  error_type TEXT NOT NULL, error_message TEXT NOT NULL,
  state TEXT NOT NULL CHECK (state IN ('OPEN','RESOLVED')), created_at TIMESTAMPTZ NOT NULL, resolved_at TIMESTAMPTZ
);
CREATE INDEX ON incident (instance_key, state);

CREATE TABLE user_task (
  key BIGINT PRIMARY KEY, instance_key BIGINT NOT NULL, element_instance_key BIGINT NOT NULL,
  assignee TEXT, candidate_groups TEXT[] NOT NULL DEFAULT '{}', candidate_users TEXT[] NOT NULL DEFAULT '{}',
  due_date TIMESTAMPTZ, form_key TEXT, state TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE history_event (            -- append-only audit trail
  seq BIGSERIAL PRIMARY KEY, instance_key BIGINT NOT NULL, element_instance_key BIGINT,
  event_type TEXT NOT NULL, element_id TEXT, payload JSONB NOT NULL DEFAULT '{}', occurred_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX ON history_event (instance_key, seq);

CREATE TABLE outbox (
  seq BIGSERIAL PRIMARY KEY, topic TEXT NOT NULL, payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL, published_at TIMESTAMPTZ
);
CREATE INDEX ON outbox (seq) WHERE published_at IS NULL;

CREATE TABLE idempotency_key (
  key TEXT PRIMARY KEY, request_hash BYTEA NOT NULL, response JSONB NOT NULL, created_at TIMESTAMPTZ NOT NULL
);
```

- **FR-DB-001** Migrations are forward-only, numbered, and applied by `flowd migrate`; `serve` refuses to start if migrations are behind (unless `--auto-migrate`).
- **FR-DB-002** Archival: completed instances older than `retention` (default 30 d) are moved/deleted by a background janitor in batches; history retention configurable separately.

---

## 16. Concurrency & consistency

- **FR-CON-001** All state mutations for one PI are serialized by the row lock on `process_instance`. A command targeting a locked PI waits up to `lockTimeout` (5 s) then returns `503 RETRY_LATER`.
- **FR-CON-002** Cross-instance operations (message to many instances, signals, cascade cancel) MUST be split into one transaction per instance; the initiating call is idempotent and resumable (progress tracked via outbox/work items).
- **FR-CON-003** Deadlock avoidance: when a command must lock >1 PI (e.g., call activity parent/child), always lock in ascending `key` order.
- **FR-CON-004** Job activation MUST NOT lock process instances (only job rows).
- **FR-CON-005** `completeJob` locks the PI, then verifies job state; a stale worker (deadline passed and re-activated by another worker) receives `409 JOB_NOT_ACTIVATED`.

---

## 17. History, events, outbox

- **FR-HIS-001** Every element lifecycle transition, variable change, job event, incident event, timer fire, message correlation, and instance state change writes a `history_event` row in the same transaction (event types are an enum documented in `docs/events.md`).
- **FR-HIS-002** The outbox publisher (leased, `SKIP LOCKED`) delivers events to configured sinks: HTTP webhook (HMAC-signed), and optionally Kafka/NATS in a later milestone. Delivery is at-least-once; payloads include a unique `eventId` for consumer dedupe.
- **FR-HIS-003** `GET /process-instances/{key}/history` returns events ordered by `seq`.

---

## 18. Security & multi-tenancy

- **FR-SEC-001** AuthN: JWT bearer (OIDC-compatible, JWKS validation) and static API keys (hashed in DB) for workers. AuthN MUST be on by default; an explicit `--insecure-no-auth` dev flag exists.
- **FR-SEC-002** AuthZ: roles `admin`, `operator` (incidents, cancel, retries), `developer` (deploy), `worker` (jobs only), `reader`; enforced per endpoint. Roles come from JWT claim (configurable).
- **FR-SEC-003 Tenancy.** `tenantId` from token claim; every query MUST be filtered by tenant. A cross-tenant access attempt returns `404`, not `403`. Single-tenant mode = tenant `''`.
- **FR-SEC-004** Input limits: request body ≤ 4 MiB; BPMN resource ≤ 2 MiB; rate limits per API key (token bucket, configurable).
- **FR-SEC-005** Secrets never logged; variable values are NOT logged at INFO level.
- **FR-SEC-006** XML parsing MUST disable external entities/DTDs (XXE-safe).

---

## 19. Observability

- **FR-OBS-001 Metrics (Prometheus):** `flowd_process_instances_started_total`, `…_completed_total`, `…_active`, `flowd_jobs_created_total{type}`, `flowd_jobs_activated_total{type}`, `flowd_job_activation_latency_seconds{type}`, `flowd_jobs_timed_out_total`, `flowd_incidents_open`, `flowd_timers_due_lag_seconds`, `flowd_command_duration_seconds{command}`, `flowd_command_lock_wait_seconds`, `flowd_outbox_pending`.
- **FR-OBS-002 Tracing:** one trace per command; W3C `traceparent` accepted and propagated; process instance key as span attribute.
- **FR-OBS-003 Logs:** JSON, fields `ts, level, msg, instanceKey, elementId, jobKey, traceId`.

---

## 20. Non-functional requirements

- **NFR-001 Throughput target (single node, 4 vCPU, local Postgres):** ≥ 300 process-instance starts/s and ≥ 1 000 job complete/s for a 5-task linear process. Provide `bench/` scripts and publish results.
- **NFR-002 Latency:** p99 command latency < 100 ms at 50 % of the above load.
- **NFR-003 Durability:** after an acknowledged response, state survives process crash (Postgres `synchronous_commit=on`). Chaos test: kill engine mid-command → no lost or double-applied transitions.
- **NFR-004 Scalability:** adding engine nodes increases throughput until DB-bound; tested with 3 nodes.
- **NFR-005 Recovery:** startup time < 5 s regardless of data volume (no in-memory rebuild).
- **NFR-006 Test coverage:** engine core ≥ 90 % line coverage; overall ≥ 80 %.
- **NFR-007 Compatibility:** Postgres 14–17 supported.
- **NFR-008 Documentation:** README quickstart (5 min to first running instance), `docs/` for architecture, BPMN support matrix, API, ops runbook.

---

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

---

## 22. Conformance & acceptance tests (Given/When/Then)

Fixtures live in `testdata/bpmn/<name>.bpmn`. The `TestEngine` helper (in `tests/Flowd.Conformance.Tests`, in-memory store + `FakeTimeProvider`) provides: `Deploy(file)`, `Start(id, vars)`, `ActivateJob(type)`, `CompleteJob(key, vars)`, `AdvanceTime(d)`, `Publish(msg)`, `Assert.InstanceCompleted(key)`, `Assert.ActiveElements(key, ids...)`, `Assert.Variables(key, map)`, `Assert.HistoryContains(key, events...)`.

### Deployment
- **T-DEP-01** Valid single-task process deploys; returns version 1.
- **T-DEP-02** Re-deploying identical XML returns the same definition key and version 1.
- **T-DEP-03** Changed XML ⇒ version 2; starting without version uses v2; a running v1 instance still finishes on v1.
- **T-DEP-04** Unsupported element (`bpmn:complexGateway`) ⇒ 400 listing element id and reason.
- **T-DEP-05** Dangling `targetRef` ⇒ 400 with all errors collected in one response.
- **T-DEP-06** XML with external entity ⇒ rejected (XXE-safe).

### Execution
- **T-EXEC-01** `start → end`: instance completes immediately; history shows ACTIVATING/ACTIVE/COMPLETED for both events in order.
- **T-EXEC-02** `start → service(A) → end`: after Start, one job of type A exists; `CompleteJob` completes the instance.
- **T-EXEC-03** Exclusive gateway picks first true condition in document order (fixture with two true conditions).
- **T-EXEC-04** Exclusive gateway with no true condition and default flow ⇒ default taken.
- **T-EXEC-05** Exclusive gateway with no true condition and no default ⇒ incident `CONDITION_ERROR`; other branches unaffected.
- **T-EXEC-06** Parallel fork/join: two jobs created; completing one leaves join waiting; completing both continues exactly once.
- **T-EXEC-07** Parallel join with a late second token after the first cycle does not double-fire.
- **T-EXEC-08** `terminate` end event kills sibling active branches and completes the instance.
- **T-EXEC-09** Cancel instance with active job ⇒ job `CANCELED`, instance `TERMINATED`, subscriptions/timers removed.
- **T-EXEC-10** Output mapping writes to parent scope; input mapping variable is invisible outside the activity.
- **T-EXEC-11** Determinism: same command sequence ⇒ same history (excluding keys/timestamps) across 100 runs (property test).
- **T-EXEC-12** Loop-limit: model with a cycle of script tasks hits `LOOP_LIMIT_EXCEEDED` incident, engine remains responsive.

### Jobs
- **T-JOB-01** Activate returns at most `maxJobs`; each job is returned to only one worker under 20 concurrent activators (race test).
- **T-JOB-02** Job not completed before `deadline` becomes re-activatable; retries unchanged.
- **T-JOB-03** `FailJob(retries=2)` ⇒ job reactivatable after backoff with retries=2; `FailJob(retries=0)` ⇒ incident `JOB_NO_RETRIES`.
- **T-JOB-04** After `UpdateRetries(3)` + `ResolveIncident`, the job is activatable again and the incident is `RESOLVED`.
- **T-JOB-05** Completing a job twice ⇒ second call `409 JOB_NOT_ACTIVATED`.
- **T-JOB-06** Stale worker completion after re-activation by another worker ⇒ `409`.
- **T-JOB-07** `fetchVariables` limits returned variables.
- **T-JOB-08** Long-poll returns immediately when a job appears (< 200 ms) and returns empty after `requestTimeoutMs`.

### Expressions
- **T-EXPR-01..10** Table-driven: arithmetic, comparison with null, path/filter `items[price > 10]`, 1-based indexing, string builtins, `if/then/else`, date/duration parsing, error on undefined variable (→ `EXTRACT_VALUE_ERROR`), budget exhaustion error, no side effects possible.

### Sub-process / inclusive / incidents (M3)
- **T-SUB-01** Embedded sub-process runs its inner flow; variables set inside with local mapping are not visible after completion; propagated ones are.
- **T-INCL-01** Inclusive split with 2 of 3 conditions true then join fires once after both branches finish (and not before).
- **T-INCL-02** Inclusive join with one branch that never reaches the join (conditionally skipped) fires without waiting forever.
- **T-INC-01** `EXTRACT_VALUE_ERROR` on missing variable creates an incident; setting the variable via API + resolve proceeds.

### Time & messages (M4)
- **T-TMR-01** Intermediate timer `PT1H`: no progress at +59 min; progresses at +60 min.
- **T-TMR-02** Interrupting timer boundary on a service task terminates the task/job at due time and continues on the boundary path.
- **T-TMR-03** Non-interrupting cycle boundary `R3/PT10M` fires three times; the main activity continues.
- **T-TMR-04** Timer persists across engine restart; overdue timers fire on startup.
- **T-TMR-05** Two engine nodes: each timer fires exactly once.
- **T-MSG-01** Publish before subscription with ttl=1 min ⇒ buffered then correlated when instance reaches the catch event.
- **T-MSG-02** Publish with unmatched correlation key ⇒ no effect, expires after ttl.
- **T-MSG-03** Duplicate `messageId` rejected while buffered.
- **T-MSG-04** Message start event creates a new instance with message variables.
- **T-MSG-05** Message correlates to at most one instance per process definition.
- **T-EVG-01** Event-based gateway: message vs timer race — first wins, loser's timer/subscription removed.

### Errors, signals, user tasks (M5)
- **T-ERR-01** Worker `ThrowError("PAYMENT_DECLINED")` caught by matching error boundary; task terminated; flow continues.
- **T-ERR-02** Error thrown inside a sub-process caught by the sub-process's boundary (propagation up the scope chain).
- **T-ERR-03** No handler ⇒ incident `UNHANDLED_ERROR_EVENT`.
- **T-SIG-01** Broadcast signal wakes all waiting instances with matching signal catch events.
- **T-UT-01** User task create → claim by user A → complete with variables → instance continues; claim by B while claimed ⇒ 409.

### Composition (M6)
- **T-CALL-01** Call activity starts child instance; child completion resumes the parent; variable propagation per flags.
- **T-CALL-02** Cancelling the parent cancels child instances.
- **T-MI-01** Parallel multi-instance over `[a,b,c]` creates 3 jobs; output collection order = input order even if completed in reverse.
- **T-MI-02** Sequential multi-instance runs one at a time.
- **T-MI-03** Empty collection completes immediately with empty output.
- **T-ESP-01** Message-triggered event sub-process runs while main flow continues (non-interrupting) / interrupts (interrupting).

### Security / chaos (M7)
- **T-SEC-01** Tenant A cannot read/modify tenant B's instances (404).
- **T-SEC-02** Worker role cannot deploy or cancel instances.
- **T-CHAOS-01** SIGKILL the engine at random points during a 10 000-instance run with 20 workers; at the end every instance is `COMPLETED`, no job completed twice in history, no orphan tokens.

---

## 23. Example fixture (for agents to copy)

`testdata/bpmn/order-simple.bpmn`

```xml
<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                  xmlns:flowd="http://flowd.io/schema/1.0"
                  id="Defs_1" targetNamespace="http://example.com/bpmn">
  <bpmn:process id="order-process" isExecutable="true">
    <bpmn:startEvent id="Start"><bpmn:outgoing>F1</bpmn:outgoing></bpmn:startEvent>

    <bpmn:serviceTask id="Charge" name="Charge card">
      <bpmn:extensionElements>
        <flowd:taskDefinition type="charge-card" retries="3"/>
        <flowd:ioMapping>
          <flowd:input source="= order.total" target="amount"/>
          <flowd:output source="= receiptId" target="receiptId"/>
        </flowd:ioMapping>
      </bpmn:extensionElements>
      <bpmn:incoming>F1</bpmn:incoming><bpmn:outgoing>F2</bpmn:outgoing>
    </bpmn:serviceTask>

    <bpmn:exclusiveGateway id="Big" default="F4">
      <bpmn:incoming>F2</bpmn:incoming><bpmn:outgoing>F3</bpmn:outgoing><bpmn:outgoing>F4</bpmn:outgoing>
    </bpmn:exclusiveGateway>

    <bpmn:serviceTask id="Review" name="Manual review">
      <bpmn:extensionElements><flowd:taskDefinition type="review-order"/></bpmn:extensionElements>
      <bpmn:incoming>F3</bpmn:incoming><bpmn:outgoing>F5</bpmn:outgoing>
    </bpmn:serviceTask>

    <bpmn:endEvent id="End"><bpmn:incoming>F4</bpmn:incoming><bpmn:incoming>F5</bpmn:incoming></bpmn:endEvent>

    <bpmn:sequenceFlow id="F1" sourceRef="Start" targetRef="Charge"/>
    <bpmn:sequenceFlow id="F2" sourceRef="Charge" targetRef="Big"/>
    <bpmn:sequenceFlow id="F3" sourceRef="Big" targetRef="Review">
      <bpmn:conditionExpression>= order.total &gt; 1000</bpmn:conditionExpression>
    </bpmn:sequenceFlow>
    <bpmn:sequenceFlow id="F4" sourceRef="Big" targetRef="End"/>
    <bpmn:sequenceFlow id="F5" sourceRef="Review" targetRef="End"/>
  </bpmn:process>
</bpmn:definitions>
```

Expected behavior: with `order.total = 50` → one `charge-card` job, then instance completes. With `order.total = 5000` → additionally a `review-order` job before completion.

---

## 24. Open decisions (record resolution in `DECISIONS.md`)

1. Fixed by the user: C# 15 on .NET 11 (STS), clean architecture/SOLID, union types. Open sub-decisions: Dapper vs EF Core 11 for simple queries; build vs wrap a FEEL library for .NET; when to move from RC to GA SDK/container tags.
2. Build vs wrap a FEEL library.
3. gRPC streaming job activation in v1 or post-v1.
4. DMN business-rule task in v1 (worker-based fallback) or native decision tables post-v1.
5. Unmatched join tokens at scope completion: warn-and-complete (default) vs incident.
6. Message start event uniqueness semantics (per correlation vs per active instance).
7. Event stream sink priority (webhook only vs Kafka/NATS).

---

## 25. Out-of-scope roadmap (post-v1, informative)

Compensation & transactions, ad-hoc sub-processes, DMN engine, process instance migration between versions, instance modification (token move/add/cancel), BPMN diagram viewer UI with live heat-map, Elasticsearch-style history search, partitioned multi-writer scaling (Zeebe-style), Helm chart & Kubernetes operator.
