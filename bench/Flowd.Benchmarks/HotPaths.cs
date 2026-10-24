using BenchmarkDotNet.Attributes;
using Flowd.Bpmn.Model;
using Flowd.Bpmn.Reading;
using Flowd.Expressions;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Benchmarks;

/// <summary>Micro-benchmarks of the pure hot paths: expression evaluation and BPMN parsing (NFR-PERF).</summary>
[MemoryDiagnoser]
public class HotPaths
{
    private static readonly ExpressionContext Context = new(new Dictionary<string, ExpressionValue>(StringComparer.Ordinal), DateTimeOffset.UnixEpoch);
    private byte[] _bpmn = [];

    [GlobalSetup]
    public void Setup() => _bpmn = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "order-simple.bpmn"));

    [Benchmark]
    public EvaluationResult Evaluate_arithmetic_condition() => FeelEvaluator.Evaluate("= 1 + 2 * 3 > 5 and true", Context);

    [Benchmark]
    public BpmnReadResult Parse_and_validate_process()
    {
        using var stream = new MemoryStream(_bpmn);
        return BpmnReader.Read(stream);
    }
}
