# Spec index (Flowd)

Split of the original spec (`AGENT_BRIEF.md` PART 2). Requirement IDs and numbering are unchanged. Read only the file(s) your slice names (TOK-001).

| File | Spec sections | Read when |
|---|---|---|
| `01-rules.md` | §0 (git/test gate, token rules, planning) | always (§0, 0.1) |
| `02-overview.md` | §1 goals, non-goals, compatibility | scope questions |
| `03-stack-architecture.md` | §2 stack/CC/unions/layout, §3 glossary, §4 architecture, §5 domain model | structure, types, ports |
| `04-bpmn-semantics.md` | §6 element matrix + FR-EXEC, §7 interpreter | implementing an element |
| `05-expressions.md` | §8 flowd-FEEL, variables, mappings | expressions, FR-VAR |
| `06-jobs-timers-messages-errors.md` | §9 jobs, §10 timers, §11 messages/signals, §12 incidents/errors | FR-JOB/TMR/MSG/SIG/INC |
| `07-deployment-api.md` | §13 deployment, §14 API | FR-DEP, API-xxx |
| `08-schema-concurrency-history.md` | §15 SQL schema, §16 concurrency, §17 history/outbox | stores, locking, FR-HIS |
| `09-security-observability-nfr.md` | §18 security, §19 observability, §20 NFR | FR-SEC, FR-OBS, NFR |
| `10-milestones-ci.md` | §21 layout, milestones, Makefile, CI | current milestone entry |
| `11-conformance-tests.md` | §22 T-… tests, §23 fixture | only the matching T-… IDs |
| `12-open-decisions.md` | §24 open decisions, §25 roadmap | decisions |

Find an ID fast: `grep -rn "FR-EXEC-004" docs/spec`.
Plan and status: `PLAN.md`, `PROGRESS.md`, `DECISIONS.md`.
