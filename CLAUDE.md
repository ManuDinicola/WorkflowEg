# Flowd

BPMN 2.0 workflow engine (Camunda-like): C# 15 / .NET 11, clean architecture, SOLID, C# 15 `union` types, PostgreSQL. Spec: `docs/spec/00-index.md` (read only the files your task names). Plan: `PLAN.md`. State: `PROGRESS.md`. Decisions: `DECISIONS.md`.

## Gate (run before every commit/push; names are fixed)
- `make lint` · `make test-unit` · `make test-conformance` · `make test-integration` (persistence/API/jobs/timers/messages) · `make cover` · `make ci` (all)
- Quiet output: redirect logs to a file, show only failures/tail (TOK-007).

## Git (GIT-001..009)
- Never commit to `main` (only the bootstrap commit exists). One branch per milestone: `feat/m<N>-<slug>`; sub-branches/worktrees for parallel subagents.
- Conventional Commits with requirement IDs, e.g. `feat(engine): parallel join (FR-EXEC-004)`; small atomic commits, tests first when practical.
- No force-push, no `--no-verify`, never disable a failing test. Do not merge own PRs. Tag releases only when asked.
- PR body: what/IDs/milestone/how to test + Test report (gate output, T-… IDs).

## Tests (TEST-001..005)
- Every requirement ID touched has a test naming it; bug fix = regression test. Time via `FakeTimeProvider`, never real clocks/sleep.
- Coverage: engine core >= 90 %, overall >= 80 %.

## Token rules (TOK-001..011)
- Lean orchestrator: Grep IDs, Read with offset/limit; never read the whole spec.
- Delegate: Explore (read-only search), impl-worker (sonnet, one self-contained task), test-runner (haiku), reviewer (opus), Plan (opus, hard parts).
- Subagent brief: GOAL / REQUIREMENTS / BRANCH / FILES / CONSTRAINTS / GATE / REPORT (<=150 words).
- Parallel only on disjoint files, own worktree; verify with `make ci` yourself (TOK-006).
- Update `PROGRESS.md` (<=60 lines) in each milestone's last commit. Same failure twice -> stop delegating, fix or record blocker.

## C# conventions (CC-001..018)
- Dependencies point inward: Bpmn <- Engine <- Application <- Infrastructure/Api <- Host. Engine is pure (no I/O, clock, random, Guid.NewGuid).
- Unions (`Result<T>`, `EngineCommand`, ...) for expected failures and closed case sets; every switch over a union is exhaustive, no `_` arm (CS8509 is an error).
- `sealed` + `internal` by default; records / `readonly record struct` for domain and typed ids; Nullable on; no `!` without comment.
- Method <= 25 lines, complexity <= 10, class <= 300 lines, <= 5 ctor deps, <= 4 params. One `IElementHandler<T>` per element (open/closed).
- Async with `CancellationToken` last; `TimeProvider` + `IKeyGenerator` injected; `[LoggerMessage]` logging, never log variable values at Info.
- Ambiguity: pick the simplest option consistent with the spec, record it in `DECISIONS.md`, continue.
