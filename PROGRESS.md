# Progress

- Milestone: M2 (core execution) done on `feat/m2-core-execution` (stacked on M1, which is stacked on M0: merge in that order)
- Done: M0, M1; M2: expressions (T-EXPR), interpreter + handlers + command pipeline, Postgres stores, job queue + sweeper, instance/job/incident APIs (T-EXEC-01..12, T-JOB-01..08)
- Next: M3 (sub-process, inclusive gateway, script/manual/send tasks, incident API polish). Blocked on: gh login to open PRs
- Tools: .NET 11 RC SDK, make, gh installed. Docker Desktop installed but engine cannot start (WSL not installed): integration tests only run in CI or with FLOWD_TEST_POSTGRES
- Stop for review after M0 PR (answered: after every milestone)
- Decisions: see `DECISIONS.md`
