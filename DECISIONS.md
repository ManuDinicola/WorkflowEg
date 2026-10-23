# Decisions

Format: date · question · choice · reason.

- 2026-10-24 · Slice granularity · coarse (whole milestone: one branch + one PR per milestone; PART 1 rows are commits inside it) · user choice. Overrides the ~400-line slice cap of PLAN-002 for this project.
- 2026-10-24 · Execution style · independent work in parallel subagents (own worktree + sub-branch), merged sequentially into the milestone branch · user choice (TOK-005).
- 2026-10-24 · Review stops · after every milestone · user choice.
- 2026-10-24 · Git remote · existing `origin` https://github.com/ManuDinicola/WorkflowEg.git; push `main` (bootstrap only) and milestone branches, open PRs with `gh` · user choice.
- 2026-10-24 · SDK · .NET 11 RC SDK now, switch to GA when released · user choice. Only .NET 10.0.402 was installed at start; user installs the 11 RC before slice 0.2.
- 2026-10-24 · License · MIT (copyright 2026 manu dinicola) · simplest permissive default; change on request.
- 2026-10-24 · Spec split · `AGENT_BRIEF.md` kept in the repo root as the original; `docs/spec/*` is the working copy (PLAN-006), `PLAN.md` holds PART 1 · avoids re-reading the whole brief.
