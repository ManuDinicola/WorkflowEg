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
