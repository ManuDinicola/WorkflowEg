using System.Text.Json.Nodes;
using Flowd.Bpmn.Model;
using Flowd.Engine.State;

namespace Flowd.Engine.Execution;

/// <summary>An error boundary event that will handle the error; <see cref="Activity"/> is the attached activity to interrupt.</summary>
public sealed record BoundaryErrorHandler(BoundaryEvent Boundary, ElementInstance Activity);

/// <summary>No enclosing scope handles the error (section 12.3, step 3).</summary>
public sealed record UnhandledError;

/// <summary>Who catches a thrown BPMN error (UNI-002).</summary>
public union ErrorHandlerMatch(BoundaryErrorHandler, UnhandledError);

/// <summary>BPMN error propagation: innermost error boundary event wins, searching outwards through the scope chain (section 12.3).</summary>
public static class ErrorPropagation
{
    /// <summary>Finds the handler for <paramref name="code"/> starting at the throwing element and walking up the scopes.</summary>
    public static ErrorHandlerMatch Find(StepContext context, ElementInstance thrower, string code)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(thrower);
        var current = thrower;
        while (true)
        {
            var candidates = context.State.Graph.BoundaryEventsOf(current.ElementId)
                .Where(b => b.Definition is ErrorEventDefinition)
                .ToList();
            var match = candidates.FirstOrDefault(b => b.Definition is ErrorEventDefinition { Code: { } c } && string.Equals(c, code, StringComparison.Ordinal))
                ?? candidates.FirstOrDefault(b => b.Definition is ErrorEventDefinition { Code: null });
            if (match is not null)
            {
                return new BoundaryErrorHandler(match, current);
            }

            if (current.ScopeKey == context.State.InstanceKey || !context.State.Elements.TryGetValue(current.ScopeKey, out var parent))
            {
                return new UnhandledError();
            }

            current = parent;
        }
    }

    /// <summary>Throws the error. Returns true when a boundary event caught it: the attached activity is interrupted and flow continues from the boundary.</summary>
    public static bool Throw(StepContext context, ElementInstance thrower, string code, JsonObject? variables)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Find(context, thrower, code) is not BoundaryErrorHandler handler)
        {
            return false;
        }

        foreach (var (name, value) in variables ?? [])
        {
            if (context.WriteVariable(handler.Activity.ScopeKey, name, value, propagate: true) is { } error)
            {
                context.RaiseIncident(error.IncidentType, error.Message, thrower);
                return true; // an incident was raised instead of handing the error on
            }
        }

        TriggerCommands.TerminateTree(context, handler.Activity);
        context.Enqueue(new ActivateElement(handler.Boundary.Id, handler.Activity.ScopeKey, null));
        return true;
    }

}
