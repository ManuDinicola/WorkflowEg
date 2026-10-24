# Camunda 8 parity scorecard

Goal set by the owner: Flowd covers at least 95 % of what Camunda 8 offers. This file makes that measurable. Every line is one feature; `[x]` means implemented and covered by a test, `[ ]` means not done. Percentage = ticked lines / all lines (see the last section). Each PR updates this file.

The list is my reading of the Camunda 8 documentation (engine, BPMN, DMN, FEEL, APIs, Tasklist, Operate, Modeler, connectors, Optimize). It is a checklist I chose, not an official one; add lines if something is missing, because that makes the score honest.

## BPMN events
- [x] None start / end event
- [x] Message start event
- [x] Timer start event (date, duration, cycle)
- [x] Signal start event
- [x] Error end event
- [x] Terminate end event
- [x] Intermediate timer catch
- [x] Intermediate message catch
- [x] Intermediate signal catch / throw
- [x] Message end event (job based)
- [x] Boundary timer (interrupting, non-interrupting)
- [x] Boundary message
- [x] Boundary signal
- [x] Boundary error
- [x] Event sub-process: timer, message, signal, error start
- [x] Escalation throw / end / boundary / event sub-process
- [ ] Message throw and send task as real message publication
- [x] Link events
- [ ] Conditional events (boundary, catch, start)
- [ ] Multiple and parallel multiple events
- [ ] Compensation (events, handlers)
- [ ] Transaction sub-process and cancel event

## BPMN activities and gateways
- [x] Service task (job worker)
- [x] User task
- [x] Manual task
- [x] Script task (FEEL)
- [x] Receive task
- [x] Business rule task as worker job
- [x] Call activity (variable propagation)
- [x] Embedded sub-process
- [x] Multi-instance parallel and sequential
- [x] Exclusive gateway
- [x] Parallel gateway
- [x] Inclusive gateway
- [x] Event-based gateway
- [ ] Complex gateway
- [ ] Ad-hoc sub-process
- [ ] Business rule task with DMN
- [x] Multi-instance completion condition

## Variables, mappings, expressions
- [x] Process variables and scopes
- [x] Input / output mappings
- [x] FEEL: literals, arithmetic, comparison, boolean logic
- [x] FEEL: strings, lists, contexts (subset)
- [x] FEEL: date, time, duration (subset)
- [ ] FEEL: ranges, filters, path expressions, for / some / every
- [ ] FEEL: full built-in function library
- [ ] FEEL: user defined functions
- [ ] Variable and message payload size limits and truncation rules
- [ ] Execution listeners

## Decisions (DMN)
- [ ] Decision tables with all hit policies
- [ ] Literal expression decisions
- [ ] Decision requirements graph
- [ ] Deploy and evaluate decisions through the API
- [ ] Business rule task calling a decision

## Jobs and workers
- [x] Activate (long poll), complete, fail with retries and back-off
- [x] Throw BPMN error from a worker
- [x] Job timeout and re-delivery
- [x] Update job retries and timeout
- [x] Incidents and resolve
- [x] .NET worker SDK
- [ ] Job streaming (push)
- [ ] Worker SDK for TypeScript
- [ ] Worker SDK for Java
- [ ] Worker SDK for Python
- [ ] Fetch-variable filtering and tenant-aware activation

## Messages, signals, timers
- [x] Message publish with TTL and correlation
- [x] At-most-once delivery per definition
- [x] Signal broadcast
- [x] Timers with date, duration, cycle
- [x] Buffered messages

## Operations on running instances
- [x] Start (by id and version), cancel
- [x] Set variables
- [x] Resolve incident
- [ ] Process instance modification
- [ ] Process instance migration
- [ ] Batch operations
- [ ] Delete / archive instances and definitions on demand
- [ ] Start instance with start instructions at an element

## Human tasks
- [x] Claim, unclaim, assign, complete
- [x] Candidate groups and users, due date
- [ ] Forms (definition and submit)
- [ ] Follow-up date, priority
- [ ] Task listeners
- [ ] Task search with filters and paging

## APIs
- [x] REST API with OpenAPI document
- [x] Idempotency keys, rate limit
- [x] Search / list with paging (basic)
- [ ] Search with rich filters and sorting on all entities
- [ ] gRPC API
- [ ] Event stream / export (beyond webhooks)
- [x] Webhooks with signature (outbox)

## Security and tenancy
- [x] API keys and JWT, roles
- [x] Multi-tenancy
- [ ] OIDC login flows for UIs
- [ ] Fine-grained resource permissions
- [ ] Audit log

## Observability and operations
- [x] Prometheus metrics, OTLP traces, JSON logs
- [x] Health and readiness
- [x] Retention / janitor
- [x] Windows service and Linux deployment
- [ ] Backup and restore tooling
- [ ] Horizontal scaling proven by load tests
- [ ] Published benchmark numbers for throughput

## Tooling and UI
- [ ] Operate-like UI: definitions, instances, incidents, variables, history
- [ ] Diagram view with token overlay
- [ ] Tasklist-like UI
- [ ] Browser modeler
- [ ] Analytics (Optimize-like): durations, counts, heatmap
- [ ] Connectors: REST, webhook inbound, e-mail, queue
- [ ] Connector runtime and secrets

## Current score
Score (2026-10-24): 62 of 110 = 56 %. Target: 105 of 110 (95 %).
