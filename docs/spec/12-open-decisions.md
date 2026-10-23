## 24. Open decisions (record resolution in `DECISIONS.md`)

1. Fixed by the user: C# 15 on .NET 11 (STS), clean architecture/SOLID, union types. Open sub-decisions: Dapper vs EF Core 11 for simple queries; build vs wrap a FEEL library for .NET; when to move from RC to GA SDK/container tags.
2. Build vs wrap a FEEL library.
3. gRPC streaming job activation in v1 or post-v1.
4. DMN business-rule task in v1 (worker-based fallback) or native decision tables post-v1.
5. Unmatched join tokens at scope completion: warn-and-complete (default) vs incident.
6. Message start event uniqueness semantics (per correlation vs per active instance).
7. Event stream sink priority (webhook only vs Kafka/NATS).


## 25. Out-of-scope roadmap (post-v1, informative)

Compensation & transactions, ad-hoc sub-processes, DMN engine, process instance migration between versions, instance modification (token move/add/cancel), BPMN diagram viewer UI with live heat-map, Elasticsearch-style history search, partitioned multi-writer scaling (Zeebe-style), Helm chart & Kubernetes operator.
