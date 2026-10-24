using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// BenchmarkDotNet does not recognise the .NET 11 preview runtime yet, so the benchmarks run in-process.
var config = DefaultConfig.Instance.AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
BenchmarkRunner.Run<Flowd.Benchmarks.HotPaths>(config, args);
