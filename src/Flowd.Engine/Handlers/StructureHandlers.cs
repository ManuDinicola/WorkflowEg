using Flowd.Bpmn.Model;
using Flowd.Engine.Execution;
using Flowd.Engine.State;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Engine.Handlers;

/// <summary>A handler that must look at the whole instance after each command, e.g. joins waiting for other branches.</summary>
public interface IReevaluatingHandler
{
    /// <summary>Called when the step queue is empty. May complete elements and queue follow-up steps.</summary>
    void Reevaluate(StepContext context);
}

/// <summary>Embedded sub-process (FR-EXEC-010): a child scope that completes when nothing inside it is live.</summary>
public sealed class SubProcessHandler : ElementHandler<SubProcess>
{
    public override void Activate(StepContext context, SubProcess element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        if (context.State.Elements.Values.Any(e => e.ScopeKey == instance.Key))
        {
            return; // already started: re-running an activation after an incident must not start it twice
        }

        if (!MappingSupport.TryApplyInputs(context, element.Inputs, element.Id, instance))
        {
            return;
        }

        context.MarkActive(instance);
        if (!Triggers.OpenEventSubProcesses(context, instance, element.Id))
        {
            return;
        }

        var start = element.Nodes.OfType<StartEvent>().First(); // the validator guarantees one
        context.Enqueue(new ActivateElement(start.Id, instance.Key, null));
    }

    public override void Complete(StepContext context, SubProcess element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        context.BeginCompleting(instance);
        if (!MappingSupport.TryApplyOutputs(context, element.Outputs, propagateAll: false, [], element.Id, instance))
        {
            return;
        }

        context.ClearLocalScope(instance);
        context.Complete(instance);
        context.TakeAllOutgoing(instance);
    }
}

/// <summary>Script task (FR-EXEC-007): evaluates one expression, no code execution.</summary>
public sealed class ScriptTaskHandler : ElementHandler<ScriptTask>
{
    public override void Activate(StepContext context, ScriptTask element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        context.MarkActive(instance);
        var result = context.Evaluate(element.Expression ?? string.Empty, instance.ScopeKey);
        if (result is not EvaluationSuccess success)
        {
            context.RaiseIncident(IncidentTypes.ExtractValueError, $"Script of '{element.Id}' failed.{MappingSupport.Describe(result)}", instance);
            return;
        }

        if (context.WriteVariable(instance.ScopeKey, element.ResultVariable ?? string.Empty, ExpressionJson.ToJson(success.Value), propagate: true) is { } error)
        {
            context.RaiseIncident(error.IncidentType, error.Message, instance);
            return;
        }

        Complete(context, element, instance);
    }
}

/// <summary>
/// Inclusive gateway (FR-EXEC-005). Diverging: every true condition fires, else the default flow. Converging: fires when no other
/// live token in the scope can still reach the gateway (see docs/inclusive-join.md).
/// </summary>
public sealed class InclusiveGatewayHandler : ElementHandler<InclusiveGateway>, IReevaluatingHandler
{
    private const string TypeName = "inclusiveGateway";

    public override void Activate(StepContext context, InclusiveGateway element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        context.MarkActive(instance);
        if (context.State.Graph.Incoming(element.Id).Count <= 1)
        {
            TryFork(context, element, instance, []);
        }
    }

    public void Reevaluate(StepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var waiting = context.State.LiveElements()
            .Where(e => e.State == ElementState.Active && string.Equals(e.ElementType, TypeName, StringComparison.Ordinal) && context.State.Graph.Incoming(e.ElementId).Count > 1)
            .GroupBy(e => (e.ElementId, e.ScopeKey))
            .ToList();
        foreach (var group in waiting)
        {
            var arrivals = group.OrderBy(a => a.Key).ToList();
            var gateway = (InclusiveGateway)context.State.Graph.Node(group.Key.ElementId);
            if (HasOpenIncident(context, arrivals) || !CanFire(context, gateway, arrivals))
            {
                continue;
            }

            TryFork(context, gateway, arrivals[0], arrivals.Skip(1).ToList());
        }
    }

    /// <summary>Static reachability over the sequence flows: can a token currently at <paramref name="from"/> arrive at <paramref name="target"/>?</summary>
    public static bool CanReach(ProcessGraph graph, string from, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var pending = new Queue<string>();
        pending.Enqueue(from);
        while (pending.TryDequeue(out var current))
        {
            foreach (var flow in graph.Outgoing(current))
            {
                if (string.Equals(flow.TargetRef, target, StringComparison.Ordinal))
                {
                    return true;
                }

                if (seen.Add(flow.TargetRef))
                {
                    pending.Enqueue(flow.TargetRef);
                }
            }
        }

        return false;
    }

    private static bool HasOpenIncident(StepContext context, List<ElementInstance> arrivals) =>
        context.State.Incidents.Values.Any(i => i.State == IncidentState.Open && arrivals.Any(a => a.Key == i.ElementInstanceKey));

    private static bool CanFire(StepContext context, InclusiveGateway gateway, List<ElementInstance> arrivals)
    {
        var scope = arrivals[0].ScopeKey;
        var arrivalKeys = arrivals.Select(a => a.Key).ToHashSet();
        return !context.State.LiveElements()
            .Where(e => e.ScopeKey == scope && !arrivalKeys.Contains(e.Key))
            .Any(e => CanReach(context.State.Graph, e.ElementId, gateway.Id));
    }

    private static void TryFork(StepContext context, InclusiveGateway gateway, ElementInstance leader, List<ElementInstance> others)
    {
        var outgoing = context.State.Graph.Outgoing(gateway.Id);
        var chosen = new List<SequenceFlow>();
        if (outgoing.Count == 1 && string.IsNullOrWhiteSpace(outgoing[0].Condition))
        {
            chosen.Add(outgoing[0]);
        }
        else if (!TryChooseFlows(context, gateway, leader, outgoing, chosen))
        {
            return;
        }

        foreach (var other in others)
        {
            context.Complete(other);
        }

        context.Complete(leader);
        context.TakeFlows(leader, chosen);
    }

    private static bool TryChooseFlows(StepContext context, InclusiveGateway gateway, ElementInstance leader, IReadOnlyList<SequenceFlow> outgoing, List<SequenceFlow> chosen)
    {
        foreach (var flow in outgoing)
        {
            switch (ExclusiveGatewayHandler.Evaluate(context, flow, leader.ScopeKey))
            {
                case ConditionMatched:
                    chosen.Add(flow);
                    break;
                case ConditionNotBoolean:
                case ConditionEvaluationFailed:
                    context.RaiseIncident(IncidentTypes.ConditionError, $"Condition of flow '{flow.Id}' is invalid or did not evaluate to a boolean.", leader);
                    return false;
                case ConditionNotMatched:
                    break;
            }
        }

        if (chosen.Count > 0)
        {
            return true;
        }

        if (gateway.DefaultFlowId is { } defaultId)
        {
            chosen.Add(context.State.Graph.Flow(defaultId));
            return true;
        }

        context.RaiseIncident(IncidentTypes.ConditionError, $"No outgoing flow of gateway '{gateway.Id}' matched and there is no default flow.", leader);
        return false;
    }
}
