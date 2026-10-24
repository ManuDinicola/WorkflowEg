using System.Text.RegularExpressions;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Dmn;

/// <summary>Result of testing one input value against an input entry: matched, not matched, or an evaluation error.</summary>
internal readonly record struct TestOutcome(bool Matched, EvaluationError? Error);

/// <summary>
/// Evaluates DMN input entries (unary tests) such as <c>-</c>, <c>"gold", "silver"</c>, <c>&lt; 18</c>, <c>[18..65]</c>,
/// <c>not("x")</c> or any FEEL value, against the decision input (FR-DMN-002).
/// </summary>
internal static partial class UnaryTests
{
    public const string InputVariable = "__input";

    public static TestOutcome Matches(string tests, ExpressionContext context)
    {
        foreach (var test in TestParts.Split(tests))
        {
            var outcome = MatchesOne(test, context);
            if (outcome.Error is not null || outcome.Matched)
            {
                return outcome;
            }
        }

        return new TestOutcome(false, null);
    }

    private static TestOutcome MatchesOne(string test, ExpressionContext context)
    {
        if (test.Length == 0 || test == "-")
        {
            return new TestOutcome(true, null);
        }

        if (test.StartsWith("not(", StringComparison.Ordinal) && test.EndsWith(')'))
        {
            var inner = Matches(test[4..^1], context);
            return inner.Error is null ? new TestOutcome(!inner.Matched, null) : inner;
        }

        return Evaluate(ToExpression(test, context), context);
    }

    private static string ToExpression(string test, ExpressionContext context)
    {
        foreach (var op in new[] { "<=", ">=", "!=", "<", ">" })
        {
            if (test.StartsWith(op, StringComparison.Ordinal))
            {
                return $"= {InputVariable} {op} ({test[op.Length..]})";
            }
        }

        if (RangePattern().Match(test) is { Success: true } range)
        {
            var low = range.Groups["open"].Value == "[" ? ">=" : ">";
            var high = range.Groups["close"].Value == "]" ? "<=" : "<";
            return $"= {InputVariable} {low} ({range.Groups["low"].Value}) and {InputVariable} {high} ({range.Groups["high"].Value})";
        }

        return FeelEvaluator.Evaluate($"= {test}", context) is EvaluationSuccess { Value: ListValue }
            ? $"= {InputVariable} in ({test})"
            : $"= {InputVariable} = ({test})";
    }

    private static TestOutcome Evaluate(string expression, ExpressionContext context) =>
        FeelEvaluator.Evaluate(expression, context) switch
        {
            EvaluationSuccess { Value: bool matched } => new TestOutcome(matched, null),
            EvaluationSuccess => new TestOutcome(false, null),
            EvaluationError error => new TestOutcome(false, error),
        };

    [GeneratedRegex(@"^(?<open>[\[\(\]])\s*(?<low>.+?)\s*\.\.\s*(?<high>.+?)\s*(?<close>[\]\)\[])$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex RangePattern();
}
