using System.Text.Json.Nodes;
using Flowd.Bpmn.Model;
using Flowd.Engine.Execution;
using Flowd.Engine.State;
using Flowd.Expressions;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Engine.Handlers;

/// <summary>Input and output mappings shared by every scope-owning element (section 8.4).</summary>
internal static class MappingSupport
{
    public static string Describe(EvaluationResult result) =>
        result is EvaluationError error ? $" {error.Message}" : string.Empty;

    /// <summary>Evaluates all inputs against the parent scope, then writes them into the element's local scope.</summary>
    public static bool TryApplyInputs(StepContext context, IReadOnlyList<IoMapping> inputs, string ownerId, ElementInstance instance)
    {
        var values = new List<(string Target, JsonNode? Value)>();
        foreach (var mapping in inputs)
        {
            var evaluated = context.Evaluate(mapping.Source, instance.ScopeKey);
            if (evaluated is not EvaluationSuccess success)
            {
                context.RaiseIncident(IncidentTypes.IoMappingError, $"Input mapping '{mapping.Target}' of '{ownerId}' failed.{Describe(evaluated)}", instance);
                return false;
            }

            values.Add((mapping.Target, ExpressionJson.ToJson(success.Value)));
        }

        foreach (var (target, value) in values)
        {
            if (context.WriteVariable(instance.Key, target, value, propagate: false) is { } error)
            {
                context.RaiseIncident(error.IncidentType, error.Message, instance);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Evaluates outputs against the local scope overlaid with <paramref name="result"/> and writes them to the parent scope.
    /// Without outputs, all result variables propagate when <paramref name="propagateAll"/> is set.
    /// </summary>
    public static bool TryApplyOutputs(StepContext context, IReadOnlyList<IoMapping> outputs, bool propagateAll, JsonObject result, string ownerId, ElementInstance instance)
    {
        var writes = new List<(string Name, JsonNode? Value)>();
        if (outputs.Count > 0)
        {
            foreach (var mapping in outputs)
            {
                var evaluated = context.Evaluate(mapping.Source, instance.Key, result);
                if (evaluated is not EvaluationSuccess success)
                {
                    context.RaiseIncident(IncidentTypes.IoMappingError, $"Output mapping '{mapping.Target}' of '{ownerId}' failed.{Describe(evaluated)}", instance);
                    return false;
                }

                writes.Add((mapping.Target, ExpressionJson.ToJson(success.Value)));
            }
        }
        else if (propagateAll)
        {
            writes.AddRange(result.Select(p => (p.Key, p.Value)));
        }

        foreach (var (name, value) in writes)
        {
            if (context.WriteVariable(instance.ScopeKey, name, value, propagate: true) is { } error)
            {
                context.RaiseIncident(error.IncidentType, error.Message, instance);
                return false;
            }
        }

        return true;
    }
}
