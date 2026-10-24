# Benchmarks

Micro-benchmarks live in `bench/Flowd.Benchmarks` (BenchmarkDotNet, short job). They run in-process because BenchmarkDotNet does not recognise the .NET 11 preview runtime yet.
```
dotnet run -c Release --project bench/Flowd.Benchmarks
```
Local numbers (developer machine, Windows 11, .NET 11 RC, Release). They show orders of magnitude, not a guarantee; re-run on your hardware.

| Benchmark | Mean | Allocated |
|---|---|---|
| Evaluate `= 1 + 2 * 3 > 5 and true` | ~0.86 us | 2.3 KB |
| Parse and validate `order-simple.bpmn` | ~17.6 us | 31 KB |

Not measured yet: end-to-end throughput (it depends on PostgreSQL, one transaction per command) and an automated union boxing check (UNI-005). No throughput target is claimed here; measure with your own workers.

## End-to-end load test (measured 2026-10-24)
`bench/Flowd.Load` starts N instances of a one-step process (start event, one service task, end event) through the REST API and works the jobs with the .NET SDK. Each instance therefore costs one start, one job activation and one job completion, with history written, against PostgreSQL 16.
```
dotnet run -c Release --project bench/Flowd.Load -- --urls http://127.0.0.1:5097,http://127.0.0.1:5098 --instances 5000 --starters 24 --workers 12
```
Everything ran on one developer machine (AMD Ryzen 7 5700X, 8 cores / 16 threads, Windows 11): the engine nodes, PostgreSQL and the load generator all share the same CPU and disk. Release build, 5000 instances, 24 concurrent starters, 12 workers with 8 concurrent jobs each, rate limit raised so it does not interfere.

| Engine nodes (same database) | Instances/s | Start latency p50 / p95 / p99 | Lost or duplicated jobs |
|---|---|---|---|
| 1 | 327 | 69 / 101 / 163 ms | 0 |
| 2 | 370 | 58 / 113 / 188 ms | 0 |
| 3 | 379 | 54 / 121 / 184 ms | 0 |

What this shows, and what it does not:
- All instances completed and the workers reported exactly one result per job on every run, also with several nodes sharing one database: multi-node operation is correct under load.
- Throughput rose only 13-16 % with more nodes. With the database and the load generator on the same machine the hardware is the limit, so this does **not** prove that throughput scales with nodes. That needs a separate database server and separate load generators.
- Numbers are for this tiny process. A real process with several steps, larger variables or slow workers behaves differently; measure your own.

Operational lessons from the runs:
- Connection pools: nodes x `Maximum Pool Size` must stay below PostgreSQL `max_connections` (default 100). 3 nodes with a pool of 60 failed with `too many clients already` (HTTP 500); with a pool of 30 it ran fine.
- The default rate limit (500 requests/s, burst 1000 per node) answers 429 when a single node is driven harder than that; raise `Flowd:RateLimit:TokensPerSecond` and `Burst` for batch loads.
