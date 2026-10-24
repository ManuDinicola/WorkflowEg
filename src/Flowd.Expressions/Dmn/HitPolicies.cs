using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Dmn;

/// <summary>Turns the matching rules of a decision table into its result according to the hit policy (FR-DMN-001).</summary>
internal static class HitPolicies
{
    public static DecisionOutcome Apply(DecisionTable table, List<Rule> matched, ExpressionContext context)
    {
        if (matched.Count == 0)
        {
            return table.HitPolicy is HitPolicy.Collect or HitPolicy.RuleOrder or HitPolicy.OutputOrder
                ? Aggregate(table, [])
                : new DecisionResult(new NullValue());
        }

        var outputs = new List<ExpressionValue>();
        foreach (var rule in matched)
        {
            if (Output(table, rule, context) is not { } output)
            {
                return new DecisionFailure("DMN_OUTPUT", $"An output entry of decision '{table.Id}' failed.");
            }

            outputs.Add(output);
        }

        return table.HitPolicy switch
        {
            HitPolicy.Unique => outputs.Count == 1 ? new DecisionResult(outputs[0]) : new DecisionFailure("DMN_MULTIPLE_MATCHES", $"UNIQUE decision '{table.Id}' matched {outputs.Count} rules."),
            HitPolicy.First => new DecisionResult(outputs[0]),
            HitPolicy.Any => AnyResult(table, outputs),
            HitPolicy.Priority => new DecisionResult(Ordered(table, matched, outputs)[0]),
            HitPolicy.OutputOrder => new DecisionResult(new ListValue(Ordered(table, matched, outputs))),
            HitPolicy.Collect => Aggregate(table, outputs),
            _ => new DecisionResult(new ListValue(outputs)), // non-union: enum, RULE ORDER
        };
    }

    private static DecisionOutcome AnyResult(DecisionTable table, List<ExpressionValue> outputs) =>
        outputs.Skip(1).All(o => Same(o, outputs[0]))
            ? new DecisionResult(outputs[0])
            : new DecisionFailure("DMN_ANY_CONFLICT", $"ANY decision '{table.Id}' matched rules with different outputs.");

    private static bool Same(ExpressionValue a, ExpressionValue b) => ExpressionJson.ToJson(a)?.ToJsonString() == ExpressionJson.ToJson(b)?.ToJsonString();

    /// <summary>Single output: the value; several outputs: a context of name to value. Null when an entry fails.</summary>
    private static ExpressionValue? Output(DecisionTable table, Rule rule, ExpressionContext context)
    {
        var values = new Dictionary<string, ExpressionValue>(StringComparer.Ordinal);
        for (var i = 0; i < table.Outputs.Count && i < rule.OutputEntries.Count; i++)
        {
            var entry = rule.OutputEntries[i];
            if (entry.Length == 0)
            {
                values[table.Outputs[i].Name] = new NullValue();
            }
            else if (FeelEvaluator.Evaluate(DecisionEvaluator.Dynamic(entry), context) is EvaluationSuccess success)
            {
                values[table.Outputs[i].Name] = success.Value;
            }
            else
            {
                return null;
            }
        }

        return table.Outputs.Count == 1 ? values.Values.First() : new ContextValue(values);
    }

    /// <summary>Orders outputs by the position of the first output entry in the declared output values (highest priority first).</summary>
    private static List<ExpressionValue> Ordered(DecisionTable table, List<Rule> matched, List<ExpressionValue> outputs)
    {
        var order = table.Outputs.Count > 0 ? table.Outputs[0].Values : [];
        return [.. outputs.Select((value, index) => (value, index)).OrderBy(x => Position(order, matched[x.index].OutputEntries.FirstOrDefault() ?? string.Empty)).ThenBy(x => x.index).Select(x => x.value)];
    }

    private static int Position(IReadOnlyList<string> order, string entry)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], entry, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static DecisionOutcome Aggregate(DecisionTable table, List<ExpressionValue> outputs)
    {
        if (table.HitPolicy != HitPolicy.Collect || table.Aggregation == Aggregation.None)
        {
            return new DecisionResult(new ListValue(outputs));
        }

        if (table.Aggregation == Aggregation.Count)
        {
            return new DecisionResult((decimal)outputs.Count);
        }

        var values = new List<decimal>();
        foreach (var output in outputs)
        {
            if (output is not decimal number)
            {
                return new DecisionFailure("DMN_AGGREGATION", $"COLLECT aggregation of '{table.Id}' needs numeric outputs.");
            }

            values.Add(number);
        }

        return new DecisionResult(table.Aggregation switch
        {
            Aggregation.Sum => values.Sum(),
            Aggregation.Min => values.Count == 0 ? 0m : values.Min(),
            Aggregation.Max => values.Count == 0 ? 0m : values.Max(),
            Aggregation.Count => values.Count,
            _ => 0m, // non-union: enum
        });
    }
}
