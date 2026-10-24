using System.Text.Json.Nodes;
using Flowd.Bpmn.Model;
using Flowd.Engine.Effects;
using Flowd.Engine.Execution;
using Flowd.Engine.State;
using Flowd.Expressions;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Engine.Handlers;

/// <summary>
/// Call activity (FR-EXEC-011): asks the application to start a child instance of the called process and waits. The child's
/// variables arrive in the internal variable <see cref="ChildResultVariable"/> of the activity's local scope.
/// </summary>
public sealed class CallActivityHandler : ElementHandler<CallActivity>
{
    public const string ChildResultVariable = "_flowdChildResult";

    public override void Activate(StepContext context, CallActivity element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        var called = context.Evaluate(element.CalledElement ?? string.Empty, instance.ScopeKey);
        if (called is not EvaluationSuccess { Value: string processId } || string.IsNullOrWhiteSpace(processId))
        {
            context.RaiseIncident(IncidentTypes.ExtractValueError, $"calledElement of '{element.Id}' did not evaluate to a process id.{MappingSupport.Describe(called)}", instance);
            return;
        }

        if (!TryBuildChildVariables(context, element, instance, out var variables))
        {
            return;
        }

        context.MarkActive(instance);
        context.Emit(new ChildInstanceRequested(context.State.InstanceKey, instance.Key, processId, variables));
        context.Record(HistoryEventTypes.ChildStarted, instance, new JsonObject { ["calledElement"] = processId });
    }

    public override void Complete(StepContext context, CallActivity element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        context.BeginCompleting(instance);
        var result = context.State.Variables.InScope(instance.Key).TryGetValue(ChildResultVariable, out var stored) && stored is JsonObject obj
            ? obj
            : [];
        if (!MappingSupport.TryApplyOutputs(context, element.Outputs, element.PropagateAllChildVariables, result, element.Id, instance))
        {
            return;
        }

        context.ClearLocalScope(instance);
        context.Complete(instance);
        context.TakeAllOutgoing(instance);
    }

    private static bool TryBuildChildVariables(StepContext context, CallActivity element, ElementInstance instance, out JsonObject variables)
    {
        variables = [];
        if (element.PropagateAllParentVariables)
        {
            foreach (var (name, value) in context.State.Variables.Visible(instance.ScopeKey).Where(p => !p.Key.StartsWith(StepContext.ReservedPrefix, StringComparison.Ordinal)))
            {
                variables[name] = value?.DeepClone();
            }
        }

        foreach (var mapping in element.Inputs)
        {
            var evaluated = context.Evaluate(mapping.Source, instance.ScopeKey);
            if (evaluated is not EvaluationSuccess success)
            {
                context.RaiseIncident(IncidentTypes.IoMappingError, $"Input mapping '{mapping.Target}' of '{element.Id}' failed.{MappingSupport.Describe(evaluated)}", instance);
                return false;
            }

            variables[mapping.Target] = ExpressionJson.ToJson(success.Value);
        }

        return true;
    }
}
