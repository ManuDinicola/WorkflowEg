using Flowd.Bpmn.Model;
using Flowd.Engine.State;
using Flowd.Expressions.Evaluation;

namespace Flowd.Engine.Execution;

/// <summary>
/// Conditional events (FR-CND-001). A waiting conditional event is a subscription with a reserved name. After every command the
/// condition is evaluated in the scope of the waiting element; the first time it is true the event fires once and the
/// subscription is closed (a non-interrupting event therefore fires once, not on every change).
/// </summary>
internal static class Conditions
{
    public const string Marker = "\u0001condition";

    /// <summary>Fires every waiting conditional event whose condition is true. Returns true when something fired.</summary>
    public static bool FireTrue(StepContext context)
    {
        var fired = false;
        foreach (var subscription in context.State.Subscriptions.Values.Where(s => string.Equals(s.MessageName, Marker, StringComparison.Ordinal)).OrderBy(s => s.Key).ToList())
        {
            if (!context.State.Subscriptions.ContainsKey(subscription.Key) || ConditionOf(context, subscription) is not { } condition)
            {
                continue;
            }

            var scope = subscription.ElementInstanceKey ?? context.State.InstanceKey;
            if (context.Evaluate(condition, scope) is EvaluationSuccess { Value: true })
            {
                context.CloseSubscription(subscription);
                TriggerCommands.FireWaiting(context, subscription);
                fired = true;
            }
        }

        return fired;
    }

    private static string? ConditionOf(StepContext context, SubscriptionRecord subscription)
    {
        var node = context.State.Graph.Node(subscription.ElementId);
        var definition = node switch
        {
            IntermediateCatchEvent catchEvent => catchEvent.Definition,
            BoundaryEvent boundary => boundary.Definition,
            SubProcess sub => sub.Nodes.OfType<StartEvent>().FirstOrDefault()?.Definition,
            _ => null, // non-union
        };
        return (definition as ConditionalEventDefinition)?.Condition;
    }
}
