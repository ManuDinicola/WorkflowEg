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

        var startEvents = state.Graph.Process.Nodes.OfType<StartEvent>().ToList();
        StartEvent? start;
        if (command.StartElementId is null)
        {
            start = startEvents.Count == 1 ? startEvents[0] : null;
        }
        else
        {
            start = startEvents.FirstOrDefault(s => string.Equals(s.Id, command.StartElementId, StringComparison.Ordinal));
        }

        if (start is null)
        {
            return Rejections.Invalid(command.StartElementId is null
                ? "The process has several start events; pass startElementId."
                : $"'{command.StartElementId}' is not a start event of this process.");
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
