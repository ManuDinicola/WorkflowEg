using System.Globalization;
using System.Text.Json.Nodes;
using Flowd.Engine.Commands;
using Flowd.Engine.Effects;
using Flowd.Engine.Handlers;
using Flowd.Engine.Keys;
using Flowd.Engine.Results;
using Flowd.Engine.State;

namespace Flowd.Engine.Execution;

/// <summary>
/// The pure engine core (FR-ARCH-001): applies one <see cref="EngineCommand"/> to an <see cref="EngineState"/> and returns the
/// effects to persist. No I/O, clock or randomness; time and keys are passed in (FR-EXEC-016).
/// </summary>
public sealed class Interpreter(ElementHandlerRegistry handlers, EngineOptions? options = null)
{
    private readonly EngineOptions _options = options ?? new EngineOptions();

    public CommandOutcome Execute(EngineState state, EngineCommand command, DateTimeOffset now, IKeyGenerator keys)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(keys);
        var context = new StepContext(state, now, keys, _options);
        var rejection = Dispatch(context, command);
        if (rejection is not null)
        {
            return rejection;
        }

        RunToQuiescence(context);
        CompleteInstanceIfDone(context);
        return new CommandAccepted(context.Effects, DescribeOutcome(context));
    }

    private static CommandRejected? Dispatch(StepContext context, EngineCommand command) => command switch
    {
        StartInstance start => InstanceCommands.Start(context, start),
        CompleteJob complete => JobCommands.Complete(context, complete),
        FailJob fail => JobCommands.Fail(context, fail),
        ThrowJobError error => JobCommands.ThrowError(context, error),
        UpdateJobRetries update => JobCommands.UpdateRetries(context, update),
        CancelInstance => InstanceCommands.Cancel(context),
        SetVariables set => InstanceCommands.SetVariables(context, set),
        ModifyInstance modify => InstanceCommands.Modify(context, modify),
        ResolveIncident resolve => IncidentCommands.Resolve(context, resolve),
        TriggerTimer timer => TriggerCommands.FireTimer(context, timer),
        CorrelateMessage message => TriggerCommands.CorrelateMessage(context, message),
        TriggerSignal signal => TriggerCommands.FireSignal(context, signal),
        ClaimUserTask claim => UserTaskCommands.Claim(context, claim),
        UnclaimUserTask unclaim => UserTaskCommands.Unclaim(context, unclaim),
        AssignUserTask assign => UserTaskCommands.Assign(context, assign),
        CompleteUserTask complete => UserTaskCommands.Complete(context, complete),
        CompleteCallActivity childDone => CallActivityCommands.Complete(context, childDone),
        FailCallActivity childFailed => CallActivityCommands.Fail(context, childFailed),
        ThrowFromCallActivity childError => CallActivityCommands.ThrowError(context, childError),
    };

    /// <summary>
    /// Runs queued steps, then lets joins and scopes react to the new situation, until nothing changes.
    /// Joins that wait for other branches (inclusive) and sub-process scopes complete here, never mid-queue (FR-INT-001).
    /// </summary>
    private void RunToQuiescence(StepContext context)
    {
        while (true)
        {
            RunQueue(context);
            var before = context.Effects.Count;
            foreach (var handler in handlers.Reevaluating())
            {
                handler.Reevaluate(context);
            }

            Conditions.FireTrue(context);
            CompleteFinishedScopes(context);
            if (!context.HasQueuedSteps && context.Effects.Count == before)
            {
                return;
            }
        }
    }

    /// <summary>A sub-process completes when nothing inside it is live; parallel-join tokens that can never fire are abandoned (FR-EXEC-009).</summary>
    private void CompleteFinishedScopes(StepContext context)
    {
        var scopes = context.State.LiveElements()
            .Where(e => e.State == ElementState.Active && string.Equals(e.ElementType, "subProcess", StringComparison.Ordinal))
            .OrderByDescending(e => e.Key)
            .ToList();
        foreach (var scope in scopes)
        {
            var children = context.State.LiveElements().Where(e => e.ScopeKey == scope.Key).ToList();
            if (children.Any(c => !IsWaitingJoinToken(context, c)))
            {
                continue;
            }

            foreach (var token in children)
            {
                context.Record(HistoryEventTypes.JoinTokensAbandoned, token);
                context.Terminate(token);
            }

            var node = context.State.Graph.Node(scope.ElementId);
            handlers.For(node).Complete(context, node, scope);
        }
    }

    private void RunQueue(StepContext context)
    {
        while (context.TryDequeue(out var step))
        {
            if (step is ActivateElement && ++context.Activations > _options.MaxActivationsPerCommand)
            {
                context.RaiseIncident(IncidentTypes.LoopLimitExceeded, $"More than {_options.MaxActivationsPerCommand} element activations in one command.", null);
                context.ClearQueue();
                return;
            }

            Process(context, step);
        }
    }

    private void Process(StepContext context, Step step)
    {
        if (context.State.State != InstanceState.Active)
        {
            return; // the instance ended (e.g. an unhandled error terminated a child instance) while steps were queued
        }

        switch (step)
        {
            case ActivateElement activate:
                Activate(context, activate);
                break;
            case CompleteElement complete:
                RunOnInstance(context, complete.ElementInstanceKey, (handler, n, i) => handler.Complete(context, n, i));
                break;
            case ReactivateElement reactivate:
                RunOnInstance(context, reactivate.ElementInstanceKey, (handler, n, i) =>
                {
                    if (i.State == ElementState.Completing)
                    {
                        handler.Complete(context, n, i);
                    }
                    else if (MultiInstance.IsBody(i))
                    {
                        MultiInstance.ActivateBody(context, n, i, MultiInstance.LoopOf(n)!);
                    }
                    else if (!IsActivity(n) || Triggers.OpenBoundaries(context, n, i))
                    {
                        handler.Activate(context, n, i);
                    }
                });
                break;
        }
    }

    private void Activate(StepContext context, ActivateElement activate)
    {
        if (activate.ScopeKey != context.State.InstanceKey && !(context.State.Elements.TryGetValue(activate.ScopeKey, out var scope) && scope.IsLive))
        {
            return; // the scope ended (error, interrupting boundary) while this step was queued
        }

        var node = context.State.Graph.Node(activate.ElementId);
        var loop = MultiInstance.LoopOf(node);
        if (loop is not null && activate.LoopIndex is null)
        {
            var body = context.CreateElement(node, activate.ScopeKey, activate.IncomingFlowId, multiInstanceBody: true);
            if (Triggers.OpenBoundaries(context, node, body))
            {
                MultiInstance.ActivateBody(context, node, body, loop);
            }

            return;
        }

        var instance = context.CreateElement(node, activate.ScopeKey, activate.IncomingFlowId, activate.LoopIndex);
        if (loop is not null && !MultiInstance.PrepareChild(context, instance, loop))
        {
            return;
        }

        if (loop is null && IsActivity(node) && !Triggers.OpenBoundaries(context, node, instance))
        {
            return;
        }

        ListenerJobs.Fire(context, node, instance, "start");
        context.ActivationFromTrigger = activate.Triggered;
        handlers.For(node).Activate(context, node, instance);
        context.ActivationFromTrigger = false;
    }

    private static bool IsActivity(Bpmn.Model.FlowNode node) => node is Bpmn.Model.ServiceTask or Bpmn.Model.SubProcess or Bpmn.Model.ReceiveTask or Bpmn.Model.UserTask or Bpmn.Model.CallActivity;

    private void RunOnInstance(StepContext context, long key, Action<IElementHandler, Bpmn.Model.FlowNode, ElementInstance> action)
    {
        if (!context.State.Elements.TryGetValue(key, out var instance) || !instance.IsLive)
        {
            return;
        }

        var node = context.State.Graph.Node(instance.ElementId);
        action(handlers.For(node), node, instance);
    }

    /// <summary>
    /// The root scope completes when nothing is live (FR-EXEC-008). Parallel-join tokens that can never fire do not block it:
    /// they are abandoned with a warning event (FR-EXEC-009, DECISIONS.md).
    /// </summary>
    private static void CompleteInstanceIfDone(StepContext context)
    {
        var state = context.State;
        if (state.State != InstanceState.Active || state.Elements.Count == 0)
        {
            return;
        }

        var live = state.LiveElements().ToList();
        var blocking = live.Where(e => !IsWaitingJoinToken(context, e)).ToList();
        if (blocking.Count > 0 || state.Incidents.Values.Any(i => i.State == IncidentState.Open))
        {
            return;
        }

        foreach (var token in live)
        {
            context.Record(HistoryEventTypes.JoinTokensAbandoned, token);
            context.Terminate(token);
        }

        context.CloseTriggersOwnedBy(state.InstanceKey);
        state.State = InstanceState.Completed;
        state.EndedAt = context.Now;
        context.Emit(new InstanceStateChanged(InstanceState.Completed, context.Now));
        context.Record(HistoryEventTypes.InstanceCompleted);
        if (state is { ParentInstanceKey: { } parent, ParentElementInstanceKey: { } parentElement })
        {
            context.Emit(new ChildInstanceEnded(parent, parentElement, true, new JsonObject(state.Variables.InScope(state.InstanceKey).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())))));
        }
    }

    private static bool IsWaitingJoinToken(StepContext context, ElementInstance element) =>
        element.State == ElementState.Active
        && string.Equals(element.ElementType, "parallelGateway", StringComparison.Ordinal)
        && context.State.Graph.Incoming(element.ElementId).Count > 1;

    private static StepOutcome DescribeOutcome(StepContext context)
    {
        switch (context.State.State)
        {
            case InstanceState.Completed:
                return new Completed();
            case InstanceState.Terminated:
                return new Terminated();
            case InstanceState.Active:
                return context.RaisedIncidents.Count > 0 ? new IncidentRaised([.. context.RaisedIncidents]) : new Waiting();
        }

        return new Waiting();
    }
}

/// <summary>Builders for the refusals commands can return (CC-008).</summary>
internal static class Rejections
{
    public static CommandRejected Conflict(string code, string message) => new(new Failure(new Conflict(code, message)));

    public static CommandRejected NotFound(string message) => new(new Failure(new NotFound(message)));

    public static CommandRejected Invalid(string message) => new(new Failure(new ValidationFailure(message)));

    public static string Id(long key) => key.ToString(CultureInfo.InvariantCulture);

    public static JsonObject? CloneOrNull(JsonObject? source) => (JsonObject?)source?.DeepClone();
}
