namespace Flowd.Expressions.Values;

/// <summary>The FEEL <c>null</c> value.</summary>
public sealed record NullValue
{
    public static NullValue Instance { get; } = new();
}

/// <summary>A FEEL list.</summary>
public sealed record ListValue(IReadOnlyList<ExpressionValue> Items);

/// <summary>A FEEL context (an ordered map of names to values).</summary>
public sealed record ContextValue(IReadOnlyDictionary<string, ExpressionValue> Entries);

/// <summary>A user defined function created by <c>function(a, b) a + b</c>; it keeps the scope it was created in. Only usable inside the expression that creates it (FR-EXPR-014).</summary>
public sealed class FunctionValue
{
    internal FunctionValue(IReadOnlyList<string> parameters, object body, object closure)
    {
        Parameters = parameters;
        Body = body;
        Closure = closure;
    }

    public IReadOnlyList<string> Parameters { get; }

    internal object Body { get; }

    internal object Closure { get; }
}

/// <summary>Every value the expression language can produce (UNI-002).</summary>
public union ExpressionValue(NullValue, bool, decimal, string, DateTimeOffset, TimeSpan, ListValue, ContextValue, FunctionValue);
