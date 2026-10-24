using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Dmn;

/// <summary>A decision was evaluated; <see cref="Value"/> is the single output, an output context, or a list (FR-DMN-001).</summary>
public sealed record DecisionResult(ExpressionValue Value);

/// <summary>A decision could not be evaluated (bad expression, UNIQUE violated, ...).</summary>
public sealed record DecisionFailure(string Code, string Message);

/// <summary>Outcome of evaluating a decision.</summary>
public union DecisionOutcome(DecisionResult, DecisionFailure);

/// <summary>Evaluates decision tables (all hit policies) and literal expression decisions. Pure and side-effect free.</summary>
public static class DecisionEvaluator
{
    public static DecisionOutcome Evaluate(Decision decision, IReadOnlyDictionary<string, ExpressionValue> variables, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(variables);
        var context = new ExpressionContext(variables, now);
        return decision switch
        {
            LiteralExpressionDecision literal => FromEvaluation(FeelEvaluator.Evaluate(Dynamic(literal.Expression), context)),
            DecisionTable table => EvaluateTable(table, context),
        };
    }

    private static DecisionOutcome EvaluateTable(DecisionTable table, ExpressionContext context)
    {
        var matched = new List<Rule>();
        foreach (var rule in table.Rules)
        {
            var outcome = RuleMatches(table, rule, context);
            if (outcome.Error is not null)
            {
                return new DecisionFailure(outcome.Error.Code, outcome.Error.Message);
            }

            if (outcome.Matched)
            {
                matched.Add(rule);
            }
        }

        return HitPolicies.Apply(table, matched, context);
    }

    private static TestOutcome RuleMatches(DecisionTable table, Rule rule, ExpressionContext context)
    {
        for (var i = 0; i < table.Inputs.Count && i < rule.InputEntries.Count; i++)
        {
            if (FeelEvaluator.Evaluate(Dynamic(table.Inputs[i].Expression), context) is not EvaluationSuccess input)
            {
                return new TestOutcome(false, new EvaluationError("DMN_INPUT", $"Input expression '{table.Inputs[i].Expression}' failed."));
            }

            var outcome = UnaryTests.Matches(rule.InputEntries[i], With(context, input.Value));
            if (outcome.Error is not null || !outcome.Matched)
            {
                return outcome;
            }
        }

        return new TestOutcome(true, null);
    }

    private static ExpressionContext With(ExpressionContext context, ExpressionValue input)
    {
        var variables = new Dictionary<string, ExpressionValue>(context.Variables, StringComparer.Ordinal) { [UnaryTests.InputVariable] = input };
        return new ExpressionContext(variables, context.Now);
    }

    internal static string Dynamic(string text) => text.TrimStart().StartsWith('=') ? text : $"= {text}";

    private static DecisionOutcome FromEvaluation(EvaluationResult result) => result switch
    {
        EvaluationSuccess success => new DecisionResult(success.Value),
        EvaluationError error => new DecisionFailure(error.Code, error.Message),
    };
}
