using Flowd.Bpmn.Model;
using Flowd.Engine.Commands;
using Flowd.Engine.Effects;
using Flowd.Engine.State;

namespace Flowd.Engine.Execution;

/// <summary>Commands that act on the instance as a whole: start, cancel, set variables.</summary>
internal static class InstanceCommands
{
    public const string InstanceNotActive = "INSTANCE_NOT_ACTIVE";

    public static CommandRejected? Start(StepContext context, StartInstance command)
    {
        var state = context.State;
        if (state.State != InstanceState.Active || state.Elements.Count > 0)
        {
            return Rejections.Conflict(InstanceNotActive, "The instance was already started.");
        }

        if (ResolveStart(state, command.StartElementId) is not { } start)
        {
            return Rejections.Invalid(command.StartElementId is null
                ? "The process has several start events; pass startElementId."
                : $"'{command.StartElementId}' cannot be used as the start element of this process.");
        }

        context.Emit(new InstanceStarted(state.InstanceKey, state.DefinitionKey, state.TenantId, state.BusinessKey, state.StartedAt, state.ParentInstanceKey, state.ParentElementInstanceKey));
        context.Record(HistoryEventTypes.InstanceStarted);
        if (WriteAll(context, state.InstanceKey, command.Variables, propagate: false) is { } rejection)
        {
            return rejection;
        }

        Triggers.OpenEventSubProcesses(context, context.RootOwner, null);
        context.Enqueue(new ActivateElement(start.Id, state.InstanceKey, null));
        return null;
    }

    /// <summary>The single start event, the named start event, or (start instructions, FR-OPS-002) any activity of the process root.</summary>
    private static FlowNode? ResolveStart(EngineState state, string? startElementId)
    {
        var rootNodes = state.Graph.Process.Nodes;
        if (startElementId is null)
        {
            var startEvents = rootNodes.OfType<StartEvent>().ToList();
            return startEvents.Count == 1 ? startEvents[0] : null;
        }

        return rootNodes.FirstOrDefault(n => string.Equals(n.Id, startElementId, StringComparison.Ordinal) && n is not (BoundaryEvent or EndEvent or UnsupportedNode or SubProcess { IsEventSubProcess: true }));
    }

    public static CommandRejected? Cancel(StepContext context)
    {
        if (context.State.State != InstanceState.Active)
        {
            return Rejections.Conflict(InstanceNotActive, "The instance is not active.");
        }

        TerminateInstance(context, notifyParent: true);
        return null;
    }

    /// <summary>Terminates everything, resolves open incidents and ends the instance (FR-EXEC-015).</summary>
    public static void TerminateInstance(StepContext context, bool notifyParent)
    {
        var state = context.State;
        foreach (var element in state.LiveElements().OrderByDescending(e => e.Key).ToList())
        {
            context.Terminate(element);
        }

        foreach (var incident in state.Incidents.Values.Where(i => i.State == IncidentState.Open))
        {
            incident.State = IncidentState.Resolved;
            incident.ResolvedAt = context.Now;
            context.Emit(new IncidentChanged(incident.Copy()));
            context.Record(HistoryEventTypes.IncidentResolved);
        }

        context.CloseTriggersOwnedBy(state.InstanceKey);
        state.State = InstanceState.Terminated;
        state.EndedAt = context.Now;
        context.Emit(new InstanceStateChanged(InstanceState.Terminated, context.Now));
        context.Record(HistoryEventTypes.InstanceTerminated);
        if (notifyParent && state is { ParentInstanceKey: { } parent, ParentElementInstanceKey: { } parentElement })
        {
            context.Emit(new ChildInstanceEnded(parent, parentElement, false, []));
        }
    }

    public static CommandRejected? SetVariables(StepContext context, SetVariables command)
    {
        var state = context.State;
        if (state.State != InstanceState.Active)
        {
            return Rejections.Conflict(InstanceNotActive, "The instance is not active.");
        }

        var scope = command.ScopeKey ?? state.InstanceKey;
        var scopeExists = scope == state.InstanceKey || (state.Elements.TryGetValue(scope, out var element) && element.IsLive);
        return scopeExists
            ? WriteAll(context, scope, command.Variables, propagate: !command.Local)
            : Rejections.NotFound($"Scope {Rejections.Id(scope)} does not exist.");
    }

    /// <summary>Terminates the given live element instances, then activates the given elements in their scopes (FR-OPS-001).</summary>
    public static CommandRejected? Modify(StepContext context, ModifyInstance command)
    {
        var state = context.State;
        if (state.State != InstanceState.Active)
        {
            return Rejections.Conflict(InstanceNotActive, "The instance is not active.");
        }

        var terminate = new List<ElementInstance>();
        foreach (var key in command.TerminateElementInstanceKeys.Distinct())
        {
            if (!state.Elements.TryGetValue(key, out var element) || !element.IsLive)
            {
                return Rejections.NotFound($"Element instance {Rejections.Id(key)} is not running.");
            }

            terminate.Add(element);
        }

        var activations = new List<(string Id, long Scope)>();
        foreach (var id in command.ActivateElementIds)
        {
            if (ScopeForActivation(state, id) is not { } scope)
            {
                return Rejections.Invalid($"Element '{id}' cannot be activated: it does not exist, is a boundary or event sub-process, or its sub-process is not running.");
            }

            activations.Add((id, scope));
        }

        if (WriteAll(context, state.InstanceKey, command.Variables, propagate: false) is { } rejection)
        {
            return rejection;
        }

        foreach (var element in terminate.Where(e => e.IsLive).OrderByDescending(e => e.Key))
        {
            TriggerCommands.TerminateTree(context, element);
        }

        foreach (var (id, scope) in activations)
        {
            context.Enqueue(new ActivateElement(id, scope, null));
        }

        return null;
    }

    /// <summary>The scope key a new token for <paramref name="elementId"/> lives in: the root, or the running enclosing sub-process.</summary>
    private static long? ScopeForActivation(EngineState state, string elementId)
    {
        var node = state.Graph.Process.AllNodes().FirstOrDefault(n => string.Equals(n.Id, elementId, StringComparison.Ordinal));
        if (node is null or BoundaryEvent or UnsupportedNode or SubProcess { IsEventSubProcess: true })
        {
            return null;
        }

        if (state.Graph.ParentOf(elementId) is not { } parent)
        {
            return state.InstanceKey;
        }

        return state.LiveElements().FirstOrDefault(e => string.Equals(e.ElementId, parent, StringComparison.Ordinal))?.Key;
    }

    private static CommandRejected? WriteAll(StepContext context, long scope, System.Text.Json.Nodes.JsonObject? variables, bool propagate)
    {
        foreach (var (name, value) in variables ?? [])
        {
            if (context.WriteVariable(scope, name, value, propagate) is { } error)
            {
                return Rejections.Invalid(error.Message);
            }
        }

        return null;
    }
}
