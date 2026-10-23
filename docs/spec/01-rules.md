
# SPEC: "Flowd" — A BPMN-Based Workflow Engine (Camunda-like)

> Status: v1.0 draft · Audience: AI coding agents and human reviewers
> Working name: `flowd` (rename freely)


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


