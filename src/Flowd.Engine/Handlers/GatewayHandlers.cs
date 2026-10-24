using Flowd.Bpmn.Model;
using Flowd.Engine.Execution;
using Flowd.Engine.State;
using Flowd.Expressions;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Engine.Handlers;

/// <summary>Outcome of one sequence-flow condition (UNI-002).</summary>
public sealed record ConditionMatched;

public sealed record ConditionNotMatched;

public sealed record ConditionNotBoolean(ExpressionValue Value);

public sealed record ConditionEvaluationFailed(EvaluationError Error);

public union ConditionOutcome(ConditionMatched, ConditionNotMatched, ConditionNotBoolean, ConditionEvaluationFailed);

/// <summary>What a diverging gateway decided (UNI-002).</summary>
public sealed record TakeFlows(IReadOnlyList<SequenceFlow> Flows);

public sealed record TakeDefault(SequenceFlow Flow);

public sealed record NoFlowMatched(string Reason);

public union FlowDecision(TakeFlows, TakeDefault, NoFlowMatched);

/// <summary>Exclusive gateway: first true condition in document order wins, else the default flow (FR-EXEC-003).</summary>
public sealed class ExclusiveGatewayHandler : ElementHandler<ExclusiveGateway>
{
    public override void Activate(StepContext context, ExclusiveGateway element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        context.MarkActive(instance);
        var decision = Decide(context, element, instance);
        switch (decision)
        {
            case TakeFlows take:
                Pass(context, instance, take.Flows);
                break;
            case TakeDefault fallback:
                Pass(context, instance, [fallback.Flow]);
                break;
            case NoFlowMatched none:
                context.RaiseIncident(IncidentTypes.ConditionError, none.Reason, instance);
                break;
        }
    }

    public static ConditionOutcome Evaluate(StepContext context, SequenceFlow flow, long scope)
    {
        if (string.IsNullOrWhiteSpace(flow.Condition))
        {
            return new ConditionNotMatched();
        }

        var result = context.Evaluate(flow.Condition, scope);
        return result switch
        {
            EvaluationError error => new ConditionEvaluationFailed(error),
            EvaluationSuccess { Value: true } => new ConditionMatched(),
            EvaluationSuccess { Value: false } => new ConditionNotMatched(),
            EvaluationSuccess success => new ConditionNotBoolean(success.Value),
        };
    }

    private static void Pass(StepContext context, ElementInstance instance, IEnumerable<SequenceFlow> flows)
    {
        context.Complete(instance);
        context.TakeFlows(instance, flows);
    }

    private static FlowDecision Decide(StepContext context, ExclusiveGateway gateway, ElementInstance instance)
    {
        var outgoing = context.State.Graph.Outgoing(gateway.Id);
        if (outgoing.Count == 1 && string.IsNullOrWhiteSpace(outgoing[0].Condition))
        {
            return new TakeFlows(outgoing);
        }

        foreach (var flow in outgoing)
        {
            var outcome = Evaluate(context, flow, instance.ScopeKey);
            switch (outcome)
            {
                case ConditionMatched:
                    return new TakeFlows([flow]);
                case ConditionNotBoolean notBoolean:
                    return new NoFlowMatched($"Condition of flow '{flow.Id}' did not evaluate to a boolean (got {ExpressionFormatter.ToText(notBoolean.Value)}).");
                case ConditionEvaluationFailed failed:
                    return new NoFlowMatched($"Condition of flow '{flow.Id}' failed: {failed.Error.Message}");
                case ConditionNotMatched:
                    break;
            }
        }

        return gateway.DefaultFlowId is { } defaultId
            ? new TakeDefault(context.State.Graph.Flow(defaultId))
            : new NoFlowMatched($"No outgoing flow of gateway '{gateway.Id}' matched and there is no default flow.");
    }
}

/// <summary>Parallel gateway: fork on all outgoing flows, join when every incoming flow has a token (FR-EXEC-004).</summary>
public sealed class ParallelGatewayHandler : ElementHandler<ParallelGateway>
{
    public override void Activate(StepContext context, ParallelGateway element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        context.MarkActive(instance);
        var incoming = context.State.Graph.Incoming(element.Id);
        if (incoming.Count <= 1)
        {
            Complete(context, element, instance);
            return;
        }

        var consumed = TryConsumeOnePerIncomingFlow(context, element, instance, incoming);
        if (consumed.Count == 0)
        {
            return; // wait: the arrival stays ACTIVE as a queued token
        }

        foreach (var token in consumed.Where(t => t.Key != instance.Key))
        {
            context.Complete(token);
        }

        Complete(context, element, instance);
    }

    private static List<ElementInstance> TryConsumeOnePerIncomingFlow(
        StepContext context,
        ParallelGateway gateway,
        ElementInstance arrival,
        IReadOnlyList<SequenceFlow> incoming)
    {
        var waiting = context.State.LiveElements()
            .Where(e => string.Equals(e.ElementId, gateway.Id, StringComparison.Ordinal) && e.ScopeKey == arrival.ScopeKey && e.State == ElementState.Active)
            .ToList();
        var picked = new List<ElementInstance>();
        foreach (var flow in incoming)
        {
            var token = waiting.FirstOrDefault(w => string.Equals(w.IncomingFlowId, flow.Id, StringComparison.Ordinal));
            if (token is null)
            {
                return [];
            }

            picked.Add(token);
        }

        return picked;
    }
}
