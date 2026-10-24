# Progress

- Milestone: M7 (production readiness) done on `feat/m7-production-readiness` (stacked on M6)
- Done: M0-M2 merged (v0.2.0); M3-M5 in PRs #6-#8; M6 in PR #9; M7: security, limits, observability, outbox, retention, worker SDK, OpenAPI snapshot, chaos test, benchmarks, docs
- Next: user merges PRs in order (#6, #7, #8, #9, then M7); then tag a release when asked
- Tools: .NET 11 RC SDK, make, gh installed. Docker cannot start locally (WSL missing): integration tests run in CI or with FLOWD_TEST_POSTGRES
- Not verified: install-service.ps1 on a real Windows Server; automated union boxing check (UNI-005); end-to-end throughput numbers
- Decisions: see `DECISIONS.md`
