using Flowd.Expressions.Parsing;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Evaluation;

/// <summary>Walks an expression tree. Side-effect free and bounded by a <see cref="BudgetMeter"/> (FR-EXPR-SEC-001).</summary>
internal sealed class Evaluator(BudgetMeter meter, DateTimeOffset now)
{
    public ExpressionValue Evaluate(Expr expression, Scope scope)
    {
        meter.Step();
        return expression switch
        {
            LiteralExpr literal => literal.Value,
            NameExpr name => Lookup(name.Name, scope),
            ListExpr list => new ListValue([.. list.Items.Select(i => Evaluate(i, scope))]),
            ContextExpr context => EvaluateContext(context, scope),
            UnaryExpr unary => Operators.Negate(Evaluate(unary.Operand, scope)),
            BinaryExpr binary => EvaluateBinary(binary, scope),
            PathExpr path => Member(Evaluate(path.Target, scope), path.Member),
            SubscriptExpr subscript => EvaluateSubscript(subscript, scope),
            IfExpr conditional => Evaluate(conditional.Condition, scope) is true ? Evaluate(conditional.Then, scope) : Evaluate(conditional.Otherwise, scope),
            CallExpr call => EvaluateCall(call, scope),
            BetweenExpr between => EvaluateBetween(between, scope),
            InExpr membership => EvaluateIn(membership, scope),
            QuantifiedExpr quantified => EvaluateQuantified(quantified, scope),
        };
    }

    private static ExpressionValue Lookup(string name, Scope scope) =>
        scope.TryGet(name, out var value)
            ? value
            : throw new EvaluationFailure(ExpressionErrorCodes.UndefinedVariable, $"Variable '{name}' is not defined.");

    private static ExpressionValue Member(ExpressionValue target, string member) => target switch
    {
        ContextValue context => context.Entries.TryGetValue(member, out var value) ? value : Operators.Null,
        ListValue list => new ListValue([.. list.Items.Select(i => Member(i, member))]),
        NullValue or bool or decimal or string or DateTimeOffset or TimeSpan => Operators.Null,
    };

    private ExpressionValue EvaluateContext(ContextExpr context, Scope scope)
    {
        var entries = new Dictionary<string, ExpressionValue>(StringComparer.Ordinal);
        foreach (var (key, value) in context.Entries)
        {
            entries[key] = Evaluate(value, scope);
        }

        return new ContextValue(entries);
    }

    private ExpressionValue EvaluateBinary(BinaryExpr binary, Scope scope)
    {
        var left = Evaluate(binary.Left, scope);
        var right = Evaluate(binary.Right, scope);
        return binary.Op switch
        {
            "and" => Operators.And(left, right),
            "or" => Operators.Or(left, right),
            "=" or "!=" or "<" or "<=" or ">" or ">=" => Operators.Compare(binary.Op, left, right),
            _ => Operators.Arithmetic(binary.Op, left, right), // non-union: operator text
        };
    }

    private ExpressionValue EvaluateBetween(BetweenExpr between, Scope scope)
    {
        var value = Evaluate(between.Value, scope);
        var low = Operators.Compare(">=", value, Evaluate(between.Low, scope));
        var high = Operators.Compare("<=", value, Evaluate(between.High, scope));
        return Operators.And(low, high);
    }

    private ExpressionValue EvaluateQuantified(QuantifiedExpr quantified, Scope scope)
    {
        if (Evaluate(quantified.Source, scope) is not ListValue list)
        {
            return Operators.Null;
        }

        var results = list.Items.Select(item => Evaluate(quantified.Body, new Scope(new Dictionary<string, ExpressionValue>(StringComparer.Ordinal) { [quantified.Variable] = item }, scope))).ToList();
        return quantified.Kind switch
        {
            "for" => new ListValue(results),
            "some" => results.Any(r => r is true),
            _ => results.All(r => r is true), // non-union: every
        };
    }

    private ExpressionValue EvaluateIn(InExpr membership, Scope scope)
    {
        var value = Evaluate(membership.Value, scope);
        var collection = Evaluate(membership.Collection, scope);
        return collection is ListValue list ? list.Items.Any(i => Operators.ValuesEqual(value, i)) : Operators.ValuesEqual(value, collection);
    }

    private ExpressionValue EvaluateCall(CallExpr call, Scope scope)
    {
        if (!Builtins.Exists(call.Function))
        {
            throw new EvaluationFailure(ExpressionErrorCodes.UnknownFunction, $"Unknown function '{call.Function}'.");
        }

        if (call.Function == "is defined")
        {
            return IsDefined(call, scope);
        }

        var arguments = call.Arguments.Select(a => Evaluate(a, scope)).ToList();
        return Builtins.Call(call.Function, arguments, now);
    }

    private bool IsDefined(CallExpr call, Scope scope)
    {
        try
        {
            return call.Arguments.Count == 1 && Evaluate(call.Arguments[0], scope) is not NullValue;
        }
        catch (EvaluationFailure ex) when (ex.Code == ExpressionErrorCodes.UndefinedVariable)
        {
            return false;
        }
    }

    private ExpressionValue EvaluateSubscript(SubscriptExpr subscript, Scope scope)
    {
        var target = Evaluate(subscript.Target, scope);
        var items = target is ListValue list ? list.Items : [target];
        if (TryEvaluateIndex(subscript.Selector, scope) is { } index)
        {
            return PickByIndex(items, index);
        }

        var kept = items.Where(item => Evaluate(subscript.Selector, ItemScope(item, scope)) is true).ToList();
        return new ListValue(kept);
    }

    /// <summary>A selector that evaluates to a number in the outer scope is an index; anything else is a per-item filter.</summary>
    private decimal? TryEvaluateIndex(Expr selector, Scope scope)
    {
        try
        {
            return Evaluate(selector, scope) is decimal d ? d : null;
        }
        catch (EvaluationFailure ex) when (ex.Code == ExpressionErrorCodes.UndefinedVariable)
        {
            return null;
        }
    }

    private static ExpressionValue PickByIndex(IReadOnlyList<ExpressionValue> items, decimal index)
    {
        var position = index > 0 ? (int)index - 1 : items.Count + (int)index;
        return position >= 0 && position < items.Count ? items[position] : Operators.Null;
    }

    private static Scope ItemScope(ExpressionValue item, Scope parent)
    {
        var bindings = new Dictionary<string, ExpressionValue>(StringComparer.Ordinal) { ["item"] = item };
        if (item is ContextValue context)
        {
            foreach (var entry in context.Entries)
            {
                bindings[entry.Key] = entry.Value;
            }
        }

        return new Scope(bindings, parent);
    }
}
