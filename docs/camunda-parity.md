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
- [x] Message throw events (intermediate and end) publish real messages; send task stays a worker job
- [x] Link events
- [x] Conditional events (boundary, catch, event sub-process; fire once)
- [ ] Conditional start events
- [x] Compensation (simplified: boundary event with handler task, intermediate and end throw; handlers start together, the throw does not wait)

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
- [x] Ad-hoc sub-process (all free activities start; no completion condition or dynamic selection)
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
- [x] FEEL: date and time arithmetic, properties (.year .hour .days ...) and date functions
- [x] FEEL: time-of-day type and years-and-months durations
- [x] FEEL: the rarer standard functions (stddev, variance, mode, trim, is blank, is empty, decimal, list replace, get entries, week of year, to json, from json, to base64)
- [x] FEEL: user defined functions (defined and called inside one expression; they cannot be stored in variables)
- [x] Variable and message payload size limits and truncation rules
- [x] Execution listeners (non-blocking jobs: start and end; Camunda 8.6 listeners can block, Flowd's cannot)

## Decisions (DMN)
- [x] Decision tables with all hit policies
- [x] Literal expression decisions
- [x] Decision requirements graph (decisions requiring decisions in one file)
- [x] Deploy and evaluate decisions through the API
- [x] Business rule task calling a decision

## Jobs and workers
- [x] Activate (long poll), complete, fail with retries and back-off
- [x] Throw BPMN error from a worker
- [x] Job timeout and re-delivery
- [x] Update job retries and timeout
- [x] Incidents and resolve
- [x] .NET worker SDK
- [x] Job streaming (push)
- [x] Worker SDK for TypeScript
- [x] Worker SDK for Java (JDK only, no dependencies)
- [x] Worker SDK for Python
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
- [x] Delete process definitions on demand
- [x] Start instance with start instructions at an element

## Human tasks
- [x] Claim, unclaim, assign, complete
- [x] Candidate groups and users, due date
- [x] Forms (definition and submit)
- [x] Follow-up date, priority
- [x] Task listeners (created, assigned, completed on user tasks; non-blocking jobs)
- [x] Task search with filters and paging

## APIs
- [x] REST API with OpenAPI document
- [x] Idempotency keys, rate limit
- [x] Search / list with paging (basic)
- [x] Search with rich filters on instances, jobs, tasks and incidents; newest-first sorting on instances
- [x] gRPC API (start instance, activate, complete and fail jobs, publish message)
- [x] Event stream / export (beyond webhooks)
- [x] Webhooks with signature (outbox)

## Security and tenancy
- [x] API keys and JWT, roles
- [x] Multi-tenancy
- [x] OIDC login flows for UIs (authorization code with PKCE in the browser; no refresh token; not yet run against a real provider)
- [x] Fine-grained permissions: API keys limited to process ids (deploy, start, batch)
- [x] Fine-grained permissions on instance routes and instance lists (jobs, incidents and tasks by key stay tenant-scoped)
- [x] Audit log

## Observability and operations
- [x] Prometheus metrics, OTLP traces, JSON logs
- [x] Health and readiness
- [x] Retention / janitor
- [x] Windows service and Linux deployment
- [x] Backup and restore tooling
- [x] Multi-node operation correct under load (no lost or duplicated jobs; see docs/benchmarks.md)
- [ ] Throughput scaling with nodes demonstrated on separate hardware
- [x] Published benchmark numbers for throughput

## Tooling and UI
- [x] Operate-like UI: definitions, instances, incidents, variables, history
- [x] Diagram view with token overlay
- [x] Tasklist-like UI
- [x] Browser modeler (graphical editing with a side panel for the main Flowd settings; other settings still go through the XML editor)
- [x] Browser XML editor with live diagram preview and deploy
- [x] Analytics (Optimize-like): durations, counts, heatmap
- [x] Connectors: REST (outbound) and signed webhook (inbound)
- [x] Connector: e-mail (SMTP)
- [x] Connector: message queue (RabbitMQ only; no Kafka or cloud queues)
- [x] Connector runtime and secrets

## Current score
Score (2026-10-25): 123 of 125 = 98.4 %. Target: 98 % (123 of 125).

Not counted: complex gateway, transaction sub-process with cancel event, and multiple / parallel multiple events. Camunda 8 does not support them either (they are BPMN elements that Camunda 8 leaves out), so they are not Camunda 8 features. Flowd does not support them. Before 2026-10-25 these three lines were in the checklist as open items; moving them out raised the percentage by about three points without any new code, so compare scores before and after with that in mind.

## Audit of what the first checklist missed (2026-10-25)
Features of Camunda 8 that were not in the checklist above. They count in the score from now on.
- [x] Create an instance and wait for its result (with fetched variables)
- [x] Business knowledge models (BKM) in DMN (literal expression logic only)
- [x] Element templates in the modeler (built-in HTTP, e-mail and decision templates; no import of custom template files)
- [x] Form builder (graphical) in the UI (six field types; no groups or conditional fields)
- [x] DMN decision table editor in the UI (one table per file)
- [x] Resource-based authorizations: processes (key limits) and user tasks (groups); not decisions, forms or incidents
- [x] Users, groups and roles: built-in users with passwords, roles, groups and tenants (no password reset, no 2FA); API keys; or your identity provider

## Second audit: Camunda 8 features the first two lists did not cover (2026-10-25)
The first list was too narrow: Flowd scored 98 % on it while there were still many Camunda 8 features nobody had looked at. This list adds them. Items are ticked only when Flowd has them, checked in the code or by a test.

### BPMN elements and execution
- [x] Intermediate signal throw and catch events
- [x] Escalation end event
- [x] Script task with a FEEL expression and result variable
- [x] Boundary events on call activities, and multi-instance call activities
- [x] Job type, retries and headers given as expressions
- [x] Signal end event
- [x] Version tags on process definitions, and call activities that bind to a version tag (not the "deployment" binding)
- [ ] Process instance tags
- [x] Call activity with the called process id as an expression

### FEEL
- [x] `instance of` and type checks (dates and dates with a time are one kind of value here)
- [x] `before` and `after` on single values
- [ ] Range values and the range functions on them (`meets`, `overlaps`, `includes`, `during`, ...)
- [x] `sort` with a comparison function
- [x] Temporal literals such as `@"2026-10-25"`
- [x] `day of year`, `date and time(date, time)`, `number(text, grouping, decimal)`, `format number`
- [ ] `uuid()` and `random number()` (not planned: the engine stays deterministic, so a replay gives the same result)

### API
- [x] Search of instances, jobs, tasks, incidents, variables and element instances
- [x] Process definition statistics per element
- [x] Validate a BPMN file through the API without deploying it
- [x] Topology: which nodes are running
- [x] Decision instance history (which decision was evaluated with what)
- [x] Command line deploy and start (`flowd deploy`, `flowd start`)

### Connectors
- [x] Outbound HTTP, e-mail and RabbitMQ; inbound signed webhooks; secrets
- [x] REST connector with OAuth 2.0 client credentials and basic/bearer helpers
- [x] GraphQL connector
- [x] Slack connector (posting messages only)
- [x] Kafka connector (producing only)
- [x] SQL connector (PostgreSQL only)
- [ ] AWS (SQS, SNS, Lambda) connectors
- [ ] Azure and Google connectors

### Modeler and tools
- [x] BPMN modeler, decision table editor, form builder, element templates for HTTP, e-mail and decisions
- [x] Validation (lint) of the diagram inside the modeler (the server-side check, on demand)
- [x] Export of the diagram as an image (SVG and PNG)
- [x] Properties panel for timers, messages, signals, errors, multi-instance, input/output mappings and listeners
- [x] Import of custom element template files (kept in the browser, for the built-in connector types)
- [ ] Model versions and milestones, comments, collaboration
- [ ] Play mode (run a process inside the modeler)

### Operate, Tasklist, Optimize
- [x] Instance list and detail with diagram and tokens, incidents, batch operations, task list with forms, analytics heat map
- [ ] Modify an instance (move tokens) and migrate instances from the UI
- [ ] Reports and dashboards over history (Optimize)

### Clients and developer experience
- [x] Worker SDKs for .NET, TypeScript, Python and Java; OpenAPI document
- [ ] Go client
- [ ] Spring Boot starter
- [ ] Process test library for users (assertions on instances in unit tests)

### Operations
- [x] Health, metrics, traces, logs, backup and restore (drilled in CI), Windows service, Docker image, rolling start of several nodes
- [ ] Kubernetes manifests or Helm chart
- [ ] Elasticsearch or OpenSearch exporter of events
- [ ] Partitioned, replicated engine (Zeebe-style): Flowd scales by nodes on one PostgreSQL instead
