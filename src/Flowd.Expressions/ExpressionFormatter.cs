using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Expressions;

/// <summary>Human-readable rendering of values, e.g. for incident messages.</summary>
public static class ExpressionFormatter
{
    public static string ToText(ExpressionValue value) => Builtins.ToText(value);
}
