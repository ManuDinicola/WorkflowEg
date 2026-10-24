using System.Globalization;
using System.Text.Json.Nodes;
using Flowd.Bpmn.Model;
using Flowd.Engine.Commands;
using Flowd.Engine.Effects;
using Flowd.Engine.State;
using Flowd.Expressions.Evaluation;

namespace Flowd.Engine.Execution;

/// <summary>Opens timers and message subscriptions for waiting elements (FR-TMR-001, FR-MSG-005, FR-EXEC-013/014).</summary>
internal static class Triggers
{
    /// <summary>Opens the boundary triggers of an activity that are not open yet. Idempotent so a re-activation after an incident is safe.</summary>
    public static bool OpenBoundaries(StepContext context, FlowNode activity, ElementInstance instance)
    {
        foreach (var boundary in context.State.Graph.BoundaryEventsOf(activity.Id))
        {
            if (!IsOpen(context, instance, boundary.Id) && !Open(context, instance, boundary.Id, boundary.Definition, TriggerKind.Boundary))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsOpen(StepContext context, ElementInstance owner, string elementId) =>
        context.State.Timers.Values.Any(t => t.IsActive && t.ElementInstanceKey == owner.Key && t.ElementId == elementId)
        || context.State.Subscriptions.Values.Any(s => s.ElementInstanceKey == owner.Key && s.ElementId == elementId);

    /// <summary>Evaluates the definition and opens its timer or subscription. On failure an incident is raised on <paramref name="owner"/>.</summary>
    public static bool Open(StepContext context, ElementInstance owner, string elementId, EventDefinition definition, TriggerKind kind)
    {
        switch (definition)
        {
            case TimerEventDefinition timer:
                return OpenTimer(context, owner, elementId, timer, kind);
            case MessageEventDefinition message:
                return OpenMessage(context, owner, elementId, message, kind);
        }

        context.RaiseIncident(IncidentTypes.ExtractValueError, $"Event '{elementId}' has an unsupported definition.", owner);
        return false;
    }

    private static bool OpenTimer(StepContext context, ElementInstance owner, string elementId, TimerEventDefinition timer, TriggerKind kind)
    {
        var result = context.Evaluate(timer.Expression, owner.Key);
        var text = result switch
        {
            EvaluationSuccess { Value: string s } => s,
            EvaluationSuccess { Value: DateTimeOffset d } => d.ToString("O", CultureInfo.InvariantCulture),
            EvaluationSuccess { Value: TimeSpan t } => System.Xml.XmlConvert.ToString(t),
            EvaluationSuccess or EvaluationError => null,
        };
        if (text is null || !IsoTimer.TryComputeDue(timer.Kind, text, context.Now, out var due, out var cycle))
        {
            context.RaiseIncident(IncidentTypes.ExtractValueError, $"Timer of '{elementId}' did not evaluate to a valid ISO 8601 {timer.Kind}.", owner);
            return false;
        }

        context.OpenTimer(owner, elementId, kind, due, cycle);
        return true;
    }

    private static bool OpenMessage(StepContext context, ElementInstance owner, string elementId, MessageEventDefinition message, TriggerKind kind)
    {
        var result = context.Evaluate(message.CorrelationKey ?? string.Empty, owner.Key);
        if (result is not EvaluationSuccess { Value: string key })
        {
            context.RaiseIncident(IncidentTypes.ExtractValueError, $"Correlation key of message '{message.Name}' on '{elementId}' did not evaluate to a string.", owner);
            return false;
        }

        context.OpenSubscription(owner, elementId, kind, message.Name, key);
        return true;
    }

}

/// <summary>Commands that deliver a fired timer or a correlated message to the waiting element (section 6.2, FR-EXEC-013/014).</summary>
internal static class TriggerCommands
{
    public static CommandRejected? FireTimer(StepContext context, TriggerTimer command)
    {
        if (context.State.State != InstanceState.Active)
        {
            return Rejections.Conflict(InstanceCommands.InstanceNotActive, "The instance is not active.");
        }

        if (!context.State.Timers.TryGetValue(command.TimerKey, out var timer))
        {
            return Rejections.NotFound($"Timer {Rejections.Id(command.TimerKey)} does not exist.");
        }

        if (!timer.IsActive)
        {
            return Rejections.Conflict("TIMER_NOT_ACTIVE", "The timer already fired or was cancelled.");
        }

        if (timer.DueAt > context.Now)
        {
            return Rejections.Conflict("TIMER_NOT_DUE", "The timer is not due yet.");
        }

        if (timer.Interval is { } interval && timer.Repetitions is null or > 1)
        {
            timer.DueAt += interval; // drift-free: next = previous due + interval (FR-TMR-006)
            timer.Repetitions = timer.Repetitions is { } left ? left - 1 : null;
            context.Emit(new TimerChanged(timer.Copy()));
        }
        else
        {
            context.DeactivateTimer(timer);
        }

        return Fire(context, timer.ElementInstanceKey, timer.Kind, timer.ElementId, null);
    }

    public static CommandRejected? CorrelateMessage(StepContext context, CorrelateMessage command)
    {
        if (context.State.State != InstanceState.Active)
        {
            return Rejections.Conflict(InstanceCommands.InstanceNotActive, "The instance is not active.");
        }

        if (!context.State.Subscriptions.TryGetValue(command.SubscriptionKey, out var subscription))
        {
            return Rejections.Conflict("SUBSCRIPTION_NOT_OPEN", "The subscription is no longer open.");
        }

        var rejection = Fire(context, subscription.ElementInstanceKey, subscription.Kind, subscription.ElementId, command.Variables);
        if (rejection is null)
        {
            context.Emit(new MessageCorrelated(command.MessageKey, subscription.DefinitionKey));
            context.Record(HistoryEventTypes.MessageCorrelated, null, new JsonObject { ["message"] = command.MessageName });
        }

        return rejection;
    }

    private static CommandRejected? Fire(StepContext context, long? ownerKey, TriggerKind kind, string elementId, JsonObject? variables)
    {
        if (ownerKey is not { } key || !context.State.Elements.TryGetValue(key, out var owner) || !owner.IsLive)
        {
            return Rejections.Conflict("TRIGGER_OWNER_GONE", "The element waiting for this trigger is gone.");
        }

        foreach (var (name, value) in variables ?? [])
        {
            if (context.WriteVariable(owner.ScopeKey, name, value, propagate: true) is { } error)
            {
                return Rejections.Invalid(error.Message);
            }
        }

        context.Record(HistoryEventTypes.TriggerFired, owner, new JsonObject { ["target"] = elementId });
        switch (kind)
        {
            case TriggerKind.Catch:
                context.Enqueue(new CompleteElement(owner.Key));
                break;
            case TriggerKind.Boundary:
                FireBoundary(context, owner, (BoundaryEvent)context.State.Graph.Node(elementId));
                break;
            case TriggerKind.EventGateway:
                context.Complete(owner);
                context.Enqueue(new ActivateElement(elementId, owner.ScopeKey, null, Triggered: true));
                break;
            case TriggerKind.Start:
                return Rejections.Invalid("Start triggers are handled outside an instance.");
        }

        return null;
    }

    private static void FireBoundary(StepContext context, ElementInstance activity, BoundaryEvent boundary)
    {
        if (boundary.CancelActivity)
        {
            TerminateTree(context, activity);
        }

        context.Enqueue(new ActivateElement(boundary.Id, activity.ScopeKey, null));
    }

    /// <summary>Terminates an element and everything inside it, leaves first (FR-EXEC-013).</summary>
    public static void TerminateTree(StepContext context, ElementInstance root)
    {
        var tree = new List<ElementInstance>();
        Collect(context, root.Key, tree);
        foreach (var element in tree.OrderByDescending(e => e.Key))
        {
            context.Terminate(element);
        }

        context.Terminate(root);
    }

    private static void Collect(StepContext context, long scope, List<ElementInstance> into)
    {
        foreach (var child in context.State.LiveElements().Where(e => e.ScopeKey == scope).ToList())
        {
            into.Add(child);
            Collect(context, child.Key, into);
        }
    }
}
