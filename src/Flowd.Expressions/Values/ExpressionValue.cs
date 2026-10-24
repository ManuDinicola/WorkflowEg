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

/// <summary>Every value the expression language can produce (UNI-002).</summary>
public union ExpressionValue(NullValue, bool, decimal, string, DateTimeOffset, TimeSpan, ListValue, ContextValue);
