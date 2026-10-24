using System.Diagnostics;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Evaluation;

/// <summary>Limits that keep evaluation bounded (FR-EXPR-SEC-001).</summary>
public sealed record EvaluationBudget(int MaxSteps = 100_000, TimeSpan? MaxDuration = null)
{
    public static EvaluationBudget Default { get; } = new();

    internal TimeSpan Duration => MaxDuration ?? TimeSpan.FromMilliseconds(100);
}

/// <summary>Variables and the current instant an expression is evaluated against. Time is injected, never read (FR-ARCH-001).</summary>
public sealed class ExpressionContext(IReadOnlyDictionary<string, ExpressionValue> variables, DateTimeOffset now)
{
    public IReadOnlyDictionary<string, ExpressionValue> Variables { get; } = variables;

    public DateTimeOffset Now { get; } = now;
}

/// <summary>Internal control flow: unwinds the evaluator to the public boundary, which turns it into an <see cref="EvaluationError"/>.</summary>
internal sealed class EvaluationFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Counts evaluation steps and wall time (FR-EXPR-SEC-001).</summary>
internal sealed class BudgetMeter(EvaluationBudget budget)
{
    private const int TimeCheckInterval = 256;
    private readonly long _start = Stopwatch.GetTimestamp();
    private int _steps;

    public void Step()
    {
        _steps++;
        if (_steps > budget.MaxSteps)
        {
            throw new EvaluationFailure(ExpressionErrorCodes.BudgetExceeded, $"Evaluation exceeded {budget.MaxSteps} steps.");
        }

        if (_steps % TimeCheckInterval == 0 && Stopwatch.GetElapsedTime(_start) > budget.Duration)
        {
            throw new EvaluationFailure(ExpressionErrorCodes.BudgetExceeded, "Evaluation exceeded its time budget.");
        }
    }
}

/// <summary>Chain of name bindings; inner scopes (filters) shadow outer ones.</summary>
internal sealed class Scope
{
    private readonly IReadOnlyDictionary<string, ExpressionValue> _bindings;
    private readonly Scope? _parent;

    public Scope(IReadOnlyDictionary<string, ExpressionValue> bindings, Scope? parent)
    {
        _bindings = bindings;
        _parent = parent;
    }

    public bool TryGet(string name, out ExpressionValue value)
    {
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            if (scope._bindings.TryGetValue(name, out value))
            {
                return true;
            }
        }

        value = NullValue.Instance;
        return false;
    }
}
