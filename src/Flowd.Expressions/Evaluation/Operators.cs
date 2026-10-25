using Flowd.Expressions.Values;

namespace Flowd.Expressions.Evaluation;

/// <summary>Equality, ordering and arithmetic of FEEL values.</summary>
internal static class Operators
{
    public static ExpressionValue Null => NullValue.Instance;

    public static bool IsNull(ExpressionValue value) => value is NullValue;

    public static bool ValuesEqual(ExpressionValue a, ExpressionValue b) => (a, b) switch
    {
        (NullValue, NullValue) => true,
        (bool x, bool y) => x == y,
        (decimal x, decimal y) => x == y,
        (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
        (DateTimeOffset x, DateTimeOffset y) => x == y,
        (TimeSpan x, TimeSpan y) => x == y,
        (ListValue x, ListValue y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => ValuesEqual(p.First, p.Second)),
        (ContextValue x, ContextValue y) => x.Entries.Count == y.Entries.Count
            && x.Entries.All(e => y.Entries.TryGetValue(e.Key, out var other) && ValuesEqual(e.Value, other)),
        _ => false, // non-union: tuple switch
    };

    /// <summary>Ordering for numbers, strings, instants and durations; null when the operands are not comparable.</summary>
    public static int? Compare(ExpressionValue a, ExpressionValue b) => (a, b) switch
    {
        (decimal x, decimal y) => x.CompareTo(y),
        (string x, string y) => string.CompareOrdinal(x, y),
        (DateTimeOffset x, DateTimeOffset y) => x.CompareTo(y),
        (TimeSpan x, TimeSpan y) => x.CompareTo(y),
        _ => null, // non-union: tuple switch
    };

    public static ExpressionValue Compare(string op, ExpressionValue a, ExpressionValue b)
    {
        if (op == "=")
        {
            return ValuesEqual(a, b);
        }

        if (op == "!=")
        {
            return !ValuesEqual(a, b);
        }

        var order = Compare(a, b);
        if (order is null)
        {
            return Null;
        }

        return op switch
        {
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            ">=" => order >= 0,
            _ => throw new EvaluationFailure(ExpressionErrorCodes.Syntax, $"Unknown operator '{op}'."), // non-union
        };
    }

    public static ExpressionValue And(ExpressionValue a, ExpressionValue b)
    {
        if (a is false || b is false)
        {
            return false;
        }

        return a is true && b is true ? true : Null;
    }

    public static ExpressionValue Or(ExpressionValue a, ExpressionValue b)
    {
        if (a is true || b is true)
        {
            return true;
        }

        return a is false && b is false ? false : Null;
    }

    public static ExpressionValue Arithmetic(string op, ExpressionValue a, ExpressionValue b) => (op, a, b) switch
    {
        ("+", decimal x, decimal y) => Guard(() => x + y),
        ("-", decimal x, decimal y) => Guard(() => x - y),
        ("*", decimal x, decimal y) => Guard(() => x * y),
        ("/", decimal x, decimal y) => y == 0 ? Null : Guard(() => x / y),
        ("**", decimal x, decimal y) => Power(x, y),
        ("+", string x, string y) => string.Concat(x, y),
        ("+", DateTimeOffset x, TimeSpan y) => x + y,
        ("+", TimeSpan x, DateTimeOffset y) => y + x,
        ("-", DateTimeOffset x, TimeSpan y) => x - y,
        ("-", DateTimeOffset x, DateTimeOffset y) => x - y,
        ("+", TimeSpan x, TimeSpan y) => x + y,
        ("-", TimeSpan x, TimeSpan y) => x - y,
        ("*", TimeSpan x, decimal y) => Duration(() => TimeSpan.FromTicks((long)(x.Ticks * y))),
        ("*", decimal x, TimeSpan y) => Duration(() => TimeSpan.FromTicks((long)(y.Ticks * x))),
        ("/", TimeSpan x, decimal y) => y == 0 ? Null : Duration(() => TimeSpan.FromTicks((long)(x.Ticks / y))),
        ("/", TimeSpan x, TimeSpan y) => y == TimeSpan.Zero ? Null : (decimal)x.Ticks / y.Ticks,
        _ => Null, // non-union: tuple switch; mismatched operand types yield null as in FEEL
    };

    public static ExpressionValue Negate(ExpressionValue value) => value switch
    {
        decimal d => -d,
        TimeSpan t => -t,
        NullValue or bool or string or DateTimeOffset or ListValue or ContextValue or FunctionValue => Null,
    };

    private static ExpressionValue Power(decimal x, decimal y)
    {
        var result = Math.Pow((double)x, (double)y);
        return double.IsFinite(result) && Math.Abs(result) < 7.9e27 ? (decimal)result : Null;
    }

    private static ExpressionValue Duration(Func<TimeSpan> compute)
    {
        try
        {
            return compute();
        }
        catch (OverflowException)
        {
            return Null;
        }
    }

    private static ExpressionValue Guard(Func<decimal> compute)
    {
        try
        {
            return compute();
        }
        catch (OverflowException)
        {
            return Null;
        }
    }
}
