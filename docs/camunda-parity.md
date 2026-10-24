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
- [x] Business rule task with DMN
- [x] Multi-instance completion condition

## Variables, mappings, expressions
- [x] Process variables and scopes
- [x] Input / output mappings
- [x] FEEL: literals, arithmetic, comparison, boolean logic
- [x] FEEL: strings, lists, contexts (subset)
- [x] FEEL: date, time, duration (subset)
- [x] FEEL: filters, path expressions, for / some / every
- [x] FEEL: ranges and unary tests
- [x] FEEL: common built-in functions (strings, lists, numbers, dates, contexts)
- [ ] FEEL: date and time types with arithmetic, and the remaining standard functions
- [ ] FEEL: user defined functions
- [x] Variable and message payload size limits and truncation rules
- [ ] Execution listeners

## Decisions (DMN)
- [x] Decision tables with all hit policies
- [x] Literal expression decisions
- [ ] Decision requirements graph
- [x] Deploy and evaluate decisions through the API
- [x] Business rule task calling a decision

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
- [x] Fetch-variable filtering and tenant-aware activation

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
- [x] Process instance modification
- [x] Process instance migration
- [x] Batch operations
- [x] Delete finished instances on demand
- [ ] Delete process definitions on demand
- [x] Start instance with start instructions at an element

## Human tasks
- [x] Claim, unclaim, assign, complete
- [x] Candidate groups and users, due date
- [ ] Forms (definition and submit)
- [x] Follow-up date, priority
- [ ] Task listeners
- [x] Task search with filters and paging

## APIs
- [x] REST API with OpenAPI document
- [x] Idempotency keys, rate limit
- [x] Search / list with paging (basic)
- [ ] Search with rich filters and sorting on all entities
- [ ] gRPC API
- [x] Event stream / export (beyond webhooks)
- [x] Webhooks with signature (outbox)

## Security and tenancy
- [x] API keys and JWT, roles
- [x] Multi-tenancy
- [ ] OIDC login flows for UIs
- [ ] Fine-grained resource permissions
- [x] Audit log

## Observability and operations
- [x] Prometheus metrics, OTLP traces, JSON logs
- [x] Health and readiness
- [x] Retention / janitor
- [x] Windows service and Linux deployment
- [x] Backup and restore tooling
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
Score (2026-10-24): 82 of 113 = 72 %. Target: 95 % (108 of 113).
