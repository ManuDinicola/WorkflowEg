# Decisions

Format: date · question · choice · reason.

- 2026-10-24 · Slice granularity · coarse (whole milestone: one branch + one PR per milestone; PART 1 rows are commits inside it) · user choice. Overrides the ~400-line slice cap of PLAN-002 for this project.
- 2026-10-24 · Execution style · independent work in parallel subagents (own worktree + sub-branch), merged sequentially into the milestone branch · user choice (TOK-005).
- 2026-10-24 · Review stops · after every milestone · user choice.
- 2026-10-24 · Git remote · existing `origin` https://github.com/ManuDinicola/WorkflowEg.git; push `main` (bootstrap only) and milestone branches, open PRs with `gh` · user choice.
- 2026-10-24 · SDK · .NET 11 RC SDK now, switch to GA when released · user choice. Only .NET 10.0.402 was installed at start; user installs the 11 RC before slice 0.2.
- 2026-10-24 · License · MIT (copyright 2026 manu dinicola) · simplest permissive default; change on request.
- 2026-10-24 · Spec split · `AGENT_BRIEF.md` kept in the repo root as the original; `docs/spec/*` is the working copy (PLAN-006), `PLAN.md` holds PART 1 · avoids re-reading the whole brief.
- 2026-10-24 · SDK pin · `11.0.100-rc.2.26504.105`, `rollForward: latestFeature`, `allowPrerelease: true` · newest RC at start; switch to GA when released (UNI-001: unions and `closed class` compile on default LangVersion, no preview flag needed).
- 2026-10-24 · Test runner · `Microsoft.Testing.Platform` (set in `global.json`; `dotnet test --project <p>`), no `Microsoft.NET.Test.Sdk`/`coverlet.collector` · .NET 10+ SDK dropped VSTest for `dotnet test`; xunit.v3 is MTP-native. Coverage uses `Microsoft.Testing.Extensions.CodeCoverage` (row 0.4).
- 2026-10-24 · T-BOOT-01 CS8509 check · compiled with a throwaway `dotnet build` project, not the Roslyn NuGet · Roslyn 5.9.0 cannot parse `union` yet.
- 2026-10-24 · ARC-001 discard-arm rule · syntactic test over `src/**/*.cs` (switch-expression `_ =>` arms fail unless the line has `// non-union`); no semantic analyzer in M0 · simplest option that still protects UNI-003. Uses Roslyn 5.9.0 syntax-only, so a `union` declaration may parse with recoverable errors.
- 2026-10-24 · `Failure`/`Result<T>` live in `Flowd.Engine.Results` · Engine needs them (ConditionOutcome) and Application depends on Engine, so this is the innermost shared place.
- 2026-10-24 · Analyzer tuning (`.editorconfig`) · off: CA2007, CA1303, CA1707, CA1062, CA1812, CA1815 (union structs), MA0004, MA0048, S1075, S2094 (marker classes); tests also off: CA1515, CA1861, MA0051, MA0006, MA0002, xUnit1051 · noise vs. CC rules; everything else stays at AnalysisMode=All and warnings are errors.
- 2026-10-24 · Banned APIs · `Microsoft.CodeAnalysis.BannedApiAnalyzers` + `BannedSymbols.txt` on Bpmn/Expressions/Engine (DateTime.Now/UtcNow, Guid.NewGuid, Random, File, HttpClient) · build-time half of ARC-001, which NetArchTest cannot check for member calls.
- 2026-10-24 · Deployment target · a plain Windows Server, outside Kubernetes · user requirement. Production runs `flowd.exe` as a Windows Service (`UseWindowsService`, `sc create`), PostgreSQL installed on or reachable from the server; Docker/compose are optional dev conveniences, not required to run. Prefer self-contained `dotnet publish -r win-x64` over container images; health endpoints stay for external monitors/load balancers.
- 2026-10-24 · Local integration tests · Docker Desktop cannot start here (WSL not installed, needs admin + reboot). Integration tests use Testcontainers in CI, or an existing server via `FLOWD_TEST_POSTGRES`. Not run locally yet.
- 2026-10-24 · BPMN XSD validation · skipped in M1; structural validator only (FR-DEP-004 items are all covered except "expressions parse" and "timer definitions parse", which arrive with the expression engine in M2 and timers in M4) · bundling the BPMN XSDs is large and adds little over the validator.
- 2026-10-24 · Supported BPMN in M1 · none start, none/terminate end, service task, manual task, exclusive and parallel gateway; everything else becomes `UnsupportedNode` and is rejected with its element id · matches the M2 scope; widened per milestone.
- 2026-10-24 · Multi-process files · one definition per `bpmn:process` in a file, all sharing the file checksum; versioned per `(tenant, bpmnProcessId)` under a Postgres advisory lock · simplest correct reading of FR-DEP-002.
- 2026-10-24 · `ValidationFailure.Details` · optional list of located problems; API code is `DEPLOYMENT_INVALID` when details exist, otherwise `INVALID_ARGUMENT` · one mapper (`FailureMapper`) per UNI-002.
- 2026-10-24 · Key generator · `IKeyGenerator.NextKey()` (not `Next`, CA1716) in `Flowd.Engine.Keys`; snowflake implementation with configurable `Flowd:NodeId` (0-1023) · FR-DOM-001.
- 2026-10-24 · Tenancy · all stores take a tenant id; single tenant `""` until FR-SEC-003 (M7).
