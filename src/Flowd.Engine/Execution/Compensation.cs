using Flowd.Bpmn.Model;
using Flowd.Engine.State;

namespace Flowd.Engine.Execution;

/// <summary>
/// Compensation (FR-CMP-001), simplified: a compensation throw or end event starts the handler task of every activity of its scope
/// that completed and has a compensation boundary event, latest first. Handlers start together (they are not run one after the other)
/// and a throw event does not wait for them. An activity is compensated once per completion.
/// </summary>
internal static class Compensation
{
    public static void Throw(StepContext context, ElementInstance thrower, string? activityRef)
    {
        var scope = thrower.ScopeKey;
        var completed = context.State.Elements.Values
            .Where(e => e.ScopeKey == scope && e.State == ElementState.Completed && (activityRef is null || string.Equals(e.ElementId, activityRef, StringComparison.Ordinal)))
            .OrderByDescending(e => e.Key)
            .ToList();
        foreach (var element in completed)
        {
            var handler = context.State.Graph.BoundaryEventsOf(element.ElementId)
                .Where(b => b.Definition is CompensationEventDefinition)
                .Select(b => b.CompensationHandlerId)
                .FirstOrDefault(h => h is not null);
            if (handler is not null && !Compensated(context, element, handler))
            {
                context.Enqueue(new ActivateElement(handler, scope, null));
            }
        }
    }

    private static bool Compensated(StepContext context, ElementInstance activity, string handler) =>
        context.State.Elements.Values.Any(e => e.ScopeKey == activity.ScopeKey && e.Key > activity.Key && string.Equals(e.ElementId, handler, StringComparison.Ordinal));
}
