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

    /// <summary>Evaluates a decision of a file: the decisions it requires are evaluated first (recursively) and their results are visible under their names and ids (FR-DMN-005).</summary>
    public static DecisionOutcome Evaluate(Decision decision, IReadOnlyList<Decision> graph, IReadOnlyDictionary<string, ExpressionValue> variables, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(graph);
        return EvaluateWithRequirements(decision, graph, new Dictionary<string, ExpressionValue>(variables, StringComparer.Ordinal), now, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>Evaluates a decision of a file: its business knowledge models become functions (bound by name, in file order, so a model can call the ones before it) and its required decisions are evaluated first (FR-DMN-006).</summary>
    public static DecisionOutcome Evaluate(Decision decision, DmnDefinitions definitions, IReadOnlyDictionary<string, ExpressionValue> variables, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(variables);
        var scope = new Dictionary<string, ExpressionValue>(variables, StringComparer.Ordinal);
        foreach (var model in definitions.Functions)
        {
            var source = $"= function({string.Join(", ", model.Parameters)}) {model.Expression}";
            if (FeelEvaluator.Evaluate(source, new ExpressionContext(scope, now)) is not EvaluationSuccess { Value: FunctionValue function })
            {
                return new DecisionFailure("DMN_KNOWLEDGE_MODEL", $"Business knowledge model '{model.Name}' is not a valid function.");
            }

            scope[model.Name] = function;
        }

        return Evaluate(decision, definitions.Decisions, scope, now);
    }

    private static DecisionOutcome EvaluateWithRequirements(Decision decision, IReadOnlyList<Decision> graph, Dictionary<string, ExpressionValue> variables, DateTimeOffset now, HashSet<string> path)
    {
        if (!path.Add(decision.Id))
        {
            return new DecisionFailure("DMN_CYCLE", $"Decision '{decision.Id}' requires itself through a cycle.");
        }

        foreach (var requiredId in decision.Requires)
        {
            var required = graph.FirstOrDefault(d => string.Equals(d.Id, requiredId, StringComparison.Ordinal));
            if (required is null)
            {
                return new DecisionFailure("DMN_REQUIREMENT_MISSING", $"Decision '{decision.Id}' requires '{requiredId}', which is not in the file.");
            }

            var outcome = EvaluateWithRequirements(required, graph, variables, now, path);
            if (outcome is not DecisionResult result)
            {
                return outcome;
            }

            variables[required.Id] = result.Value;
            if (!string.IsNullOrEmpty(required.Name))
            {
                variables[required.Name] = result.Value;
            }
        }

        path.Remove(decision.Id);
        return Evaluate(decision, variables, now);
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
