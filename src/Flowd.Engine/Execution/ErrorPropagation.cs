using System.Text.Json.Nodes;
using Flowd.Bpmn.Model;
using Flowd.Engine.Effects;
using Flowd.Engine.State;

namespace Flowd.Engine.Execution;

/// <summary>An error boundary event that will handle the error; <see cref="Activity"/> is the attached activity to interrupt.</summary>
public sealed record BoundaryErrorHandler(BoundaryEvent Boundary, ElementInstance Activity);

/// <summary>An error start event of an event sub-process in <see cref="ScopeKey"/> (the process root or a sub-process instance).</summary>
public sealed record EventSubProcessErrorHandler(SubProcess EventSubProcess, long ScopeKey);

/// <summary>No enclosing scope handles the error (section 12.3, step 3).</summary>
public sealed record UnhandledError;

/// <summary>Who catches a thrown BPMN error (UNI-002).</summary>
public union ErrorHandlerMatch(BoundaryErrorHandler, EventSubProcessErrorHandler, UnhandledError);

/// <summary>
/// BPMN error propagation (section 12.3): the innermost handler wins, searching the throwing activity's error boundary events,
/// then the error event sub-processes of its scope, then outwards. An error nobody handles inside a child instance continues at the
/// parent's call activity.
/// </summary>
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
            var boundary = MatchingBoundary(context, current, code);
            if (boundary is not null)
            {
                return new BoundaryErrorHandler(boundary, current);
            }

            var scopeIsRoot = current.ScopeKey == context.State.InstanceKey;
            var scopeNodeId = scopeIsRoot ? null : context.State.Elements[current.ScopeKey].ElementId;
            if (MatchingEventSubProcess(context, scopeNodeId, current.ScopeKey, code) is { } esp)
            {
                return new EventSubProcessErrorHandler(esp, current.ScopeKey);
            }

            if (scopeIsRoot || !context.State.Elements.TryGetValue(current.ScopeKey, out var parent))
            {
                return new UnhandledError();
            }

            current = parent;
        }
    }

    /// <summary>Throws the error. Returns true when something took care of it (a handler, or the parent instance); false for an unhandled error.</summary>
    public static bool Throw(StepContext context, ElementInstance thrower, string code, JsonObject? variables)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (Find(context, thrower, code))
        {
            case BoundaryErrorHandler handler:
                return Handle(context, thrower, handler.Activity.ScopeKey, variables, () =>
                {
                    TriggerCommands.TerminateTree(context, handler.Activity);
                    context.Enqueue(new ActivateElement(handler.Boundary.Id, handler.Activity.ScopeKey, null));
                });
            case EventSubProcessErrorHandler handler:
                return Handle(context, thrower, handler.ScopeKey, variables, () => TriggerCommands.StartEventSubProcess(context, handler.ScopeKey, handler.EventSubProcess));
            case UnhandledError:
                return PassToParent(context, code);
        }

        return false;
    }

    private static bool Handle(StepContext context, ElementInstance thrower, long variableScope, JsonObject? variables, Action start)
    {
        foreach (var (name, value) in variables ?? [])
        {
            if (context.WriteVariable(variableScope, name, value, propagate: true) is { } error)
            {
                context.RaiseIncident(error.IncidentType, error.Message, thrower);
                return true; // an incident was raised instead of handing the error on
            }
        }

        start();
        return true;
    }

    /// <summary>An unhandled error leaves a child instance: the child ends and the error continues at the parent's call activity.</summary>
    private static bool PassToParent(StepContext context, string code)
    {
        if (context.State is not { ParentInstanceKey: { } parent, ParentElementInstanceKey: { } parentElement })
        {
            return false;
        }

        InstanceCommands.TerminateInstance(context, notifyParent: false);
        context.Emit(new ChildErrorThrown(parent, parentElement, code));
        return true;
    }

    private static BoundaryEvent? MatchingBoundary(StepContext context, ElementInstance current, string code)
    {
        var candidates = context.State.Graph.BoundaryEventsOf(current.ElementId).Where(b => b.Definition is ErrorEventDefinition).ToList();
        return candidates.FirstOrDefault(b => b.Definition is ErrorEventDefinition { Code: { } c } && string.Equals(c, code, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault(b => b.Definition is ErrorEventDefinition { Code: null });
    }

    /// <summary>An error event sub-process of the scope that is not already running (so it cannot catch its own errors).</summary>
    private static SubProcess? MatchingEventSubProcess(StepContext context, string? scopeNodeId, long scopeKey, string code)
    {
        var candidates = context.State.Graph.EventSubProcessesIn(scopeNodeId)
            .Where(esp => !context.State.LiveElements().Any(e => e.ScopeKey == scopeKey && string.Equals(e.ElementId, esp.Id, StringComparison.Ordinal)))
            .Select(esp => (Esp: esp, Start: esp.Nodes.OfType<StartEvent>().First()))
            .Where(x => x.Start.Definition is ErrorEventDefinition)
            .ToList();
        return candidates.FirstOrDefault(x => x.Start.Definition is ErrorEventDefinition { Code: { } c } && string.Equals(c, code, StringComparison.Ordinal)).Esp
            ?? candidates.FirstOrDefault(x => x.Start.Definition is ErrorEventDefinition { Code: null }).Esp;
    }
}
