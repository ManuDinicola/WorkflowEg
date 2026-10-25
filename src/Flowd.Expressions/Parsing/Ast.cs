using Flowd.Expressions.Values;

namespace Flowd.Expressions.Parsing;

/// <summary>Expression syntax tree; a closed hierarchy so evaluators must handle every node kind (UNI-004).</summary>
internal closed class Expr;

internal sealed class LiteralExpr(ExpressionValue value) : Expr
{
    public ExpressionValue Value { get; } = value;
}

internal sealed class NameExpr(string name) : Expr
{
    public string Name { get; } = name;
}

internal sealed class ListExpr(IReadOnlyList<Expr> items) : Expr
{
    public IReadOnlyList<Expr> Items { get; } = items;
}

internal sealed class ContextExpr(IReadOnlyList<KeyValuePair<string, Expr>> entries) : Expr
{
    public IReadOnlyList<KeyValuePair<string, Expr>> Entries { get; } = entries;
}

internal sealed class UnaryExpr(string op, Expr operand) : Expr
{
    public string Op { get; } = op;

    public Expr Operand { get; } = operand;
}

internal sealed class BinaryExpr(string op, Expr left, Expr right) : Expr
{
    public string Op { get; } = op;

    public Expr Left { get; } = left;

    public Expr Right { get; } = right;
}

internal sealed class PathExpr(Expr target, string member) : Expr
{
    public Expr Target { get; } = target;

    public string Member { get; } = member;
}

/// <summary>Either a 1-based index or a filter, decided when evaluated.</summary>
internal sealed class SubscriptExpr(Expr target, Expr selector) : Expr
{
    public Expr Target { get; } = target;

    public Expr Selector { get; } = selector;
}

internal sealed class IfExpr(Expr condition, Expr then, Expr otherwise) : Expr
{
    public Expr Condition { get; } = condition;

    public Expr Then { get; } = then;

    public Expr Otherwise { get; } = otherwise;
}

internal sealed class CallExpr(string function, IReadOnlyList<Expr> arguments) : Expr
{
    public string Function { get; } = function;

    public IReadOnlyList<Expr> Arguments { get; } = arguments;
}

internal sealed class BetweenExpr(Expr value, Expr low, Expr high) : Expr
{
    public Expr Value { get; } = value;

    public Expr Low { get; } = low;

    public Expr High { get; } = high;
}

internal sealed class InExpr(Expr value, Expr collection) : Expr
{
    public Expr Value { get; } = value;

    public Expr Collection { get; } = collection;
}

/// <summary><c>some x in list satisfies cond</c>, <c>every x in list satisfies cond</c> or <c>for x in list return expr</c>.</summary>
internal sealed class QuantifiedExpr(string kind, string variable, Expr source, Expr body) : Expr
{
    public string Kind { get; } = kind;

    public string Variable { get; } = variable;

    public Expr Source { get; } = source;

    public Expr Body { get; } = body;
}

/// <summary>An interval such as <c>[1..5]</c> or <c>(1..5)</c>; only valid as the right side of <c>in</c>.</summary>
internal sealed class RangeExpr(Expr low, Expr high, bool lowInclusive, bool highInclusive) : Expr
{
    public Expr Low { get; } = low;

    public Expr High { get; } = high;

    public bool LowInclusive { get; } = lowInclusive;

    public bool HighInclusive { get; } = highInclusive;
}

internal sealed class FunctionExpr(IReadOnlyList<string> parameters, Expr body) : Expr
{
    public IReadOnlyList<string> Parameters { get; } = parameters;

    public Expr Body { get; } = body;
}
