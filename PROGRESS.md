# Progress

- Milestone: M1 (model and deploy) done on `feat/m1-model-deploy` (branched from `feat/m0-skeleton`; PRs stack: merge M0 first)
- Done: M0 (rows 0.1-0.5); M1: BPMN model/parser/validator, `flowd validate`, definitions store + versioning, deployments API (T-DEP-01..06)
- Next: M2 core execution (expressions 2.1 first, then engine 2.2+). T-DEP-03 second half (running v1 instance keeps v1) waits for instances in M2
- Tools: .NET 11 RC SDK, make, gh installed. Docker Desktop installed but engine cannot start (WSL not installed): integration tests only run in CI or with FLOWD_TEST_POSTGRES
- Stop for review after M0 PR (answered: after every milestone)
- Decisions: see `DECISIONS.md`
