using System.Text.Json.Nodes;
using Flowd.Bpmn.Model;
using Flowd.Engine.Effects;
using Flowd.Engine.Keys;
using Flowd.Engine.State;
using Flowd.Expressions;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Engine.Execution;

/// <summary>Tunable engine limits (FR-ARCH-006, FR-VAR-002).</summary>
public sealed record EngineOptions
{
    public int MaxActivationsPerCommand { get; init; } = 10_000;

    public int MaxVariableBytes { get; init; } = 1024 * 1024;

    public int MaxInstanceVariableBytes { get; init; } = 16 * 1024 * 1024;

    public int DefaultRetries { get; init; } = 3;
}

/// <summary>Why a variable write was refused; mapped to an incident type by the caller.</summary>
public sealed record VariableWriteError(string IncidentType, string Message);

/// <summary>
/// Working surface handed to element handlers while one command runs: it mutates <see cref="EngineState"/> and records the
/// matching <see cref="EngineEffect"/>s. Time and keys are injected (FR-ARCH-001).
/// </summary>
public sealed class StepContext
{
    public const string ReservedPrefix = "_flowd";

    private readonly Queue<Step> _queue = new();
    private readonly List<EngineEffect> _effects = [];
    private readonly List<long> _raisedIncidents = [];
    private readonly IKeyGenerator _keys;

    internal StepContext(EngineState state, DateTimeOffset now, IKeyGenerator keys, EngineOptions options)
    {
        State = state;
        Now = now;
        _keys = keys;
        Options = options;
    }

    public EngineState State { get; }

    public DateTimeOffset Now { get; }

    public EngineOptions Options { get; }

    internal IReadOnlyList<EngineEffect> Effects => _effects;

    internal IReadOnlyList<long> RaisedIncidents => _raisedIncidents;

    internal int Activations { get; set; }

    public long NextKey() => _keys.NextKey();

    internal void Enqueue(Step step) => _queue.Enqueue(step);

    internal bool TryDequeue(out Step step) => _queue.TryDequeue(out step);

    internal bool HasQueuedSteps => _queue.Count > 0;

    internal void ClearQueue() => _queue.Clear();

    public void Emit(EngineEffect effect) => _effects.Add(effect);

    public void Record(string eventType, ElementInstance? element = null, JsonObject? payload = null) =>
        Emit(new HistoryAppended(eventType, element?.Key, element?.ElementId, payload, Now));

    public ElementInstance CreateElement(FlowNode node, long scopeKey, string? incomingFlowId, int? loopCounter = null, bool multiInstanceBody = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        var instance = new ElementInstance
        {
            Key = NextKey(),
            ElementId = node.Id,
            ElementType = multiInstanceBody ? MultiInstance.BodyType : ElementTypes.NameOf(node),
            LoopCounter = loopCounter,
            ScopeKey = scopeKey,
            State = ElementState.Activating,
            IncomingFlowId = incomingFlowId,
            StartedAt = Now,
        };
        State.Elements[instance.Key] = instance;
        Emit(new ElementChanged(instance.Copy()));
        Record(HistoryEventTypes.ElementActivating, instance);
        return instance;
    }

    /// <summary>ACTIVATING -> ACTIVE. Idempotent so re-running an activation after an incident does not repeat history.</summary>
    public void MarkActive(ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.State != ElementState.Activating)
        {
            return;
        }

        instance.State = ElementState.Active;
        Emit(new ElementChanged(instance.Copy()));
        Record(HistoryEventTypes.ElementActive, instance);
    }

    public void BeginCompleting(ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.State == ElementState.Completing)
        {
            return;
        }

        instance.State = ElementState.Completing;
        Emit(new ElementChanged(instance.Copy()));
        Record(HistoryEventTypes.ElementCompleting, instance);
    }

    public void Complete(ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        BeginCompleting(instance);
        instance.State = ElementState.Completed;
        instance.EndedAt = Now;
        Emit(new ElementChanged(instance.Copy()));
        Record(HistoryEventTypes.ElementCompleted, instance);
        CloseTriggersOf(instance);
        ListenerJobs.Fire(this, instance, "end");
    }

    public void Terminate(ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Record(HistoryEventTypes.ElementTerminating, instance);
        instance.State = ElementState.Terminated;
        instance.EndedAt = Now;
        Emit(new ElementChanged(instance.Copy()));
        Record(HistoryEventTypes.ElementTerminated, instance);
        CloseTriggersOf(instance);
        ClearLocalScopeNow(instance);
        CancelJobsOf(instance);
        CancelUserTasksOf(instance);
        if (string.Equals(instance.ElementType, "callActivity", StringComparison.Ordinal))
        {
            Emit(new ChildCancelRequested(instance.Key));
        }
    }

    /// <summary>True while an element is being activated because its own trigger already fired (event-based gateway winner).</summary>
    public bool ActivationFromTrigger { get; internal set; }

    public TimerRecord OpenTimer(ElementInstance owner, string elementId, TriggerKind kind, DateTimeOffset due, CycleSpec? cycle)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var timer = new TimerRecord
        {
            Key = NextKey(),
            InstanceKey = State.InstanceKey,
            ElementInstanceKey = owner.Key,
            DefinitionKey = State.DefinitionKey,
            ElementId = elementId,
            Kind = kind,
            DueAt = due,
            Repetitions = cycle?.Repetitions,
            Interval = cycle?.Interval,
            TenantId = State.TenantId,
        };
        State.Timers[timer.Key] = timer;
        Emit(new TimerChanged(timer.Copy()));
        return timer;
    }

    public SubscriptionRecord OpenSubscription(ElementInstance owner, string elementId, TriggerKind kind, string messageName, string correlationKey, bool isSignal = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var subscription = new SubscriptionRecord
        {
            Key = NextKey(),
            InstanceKey = State.InstanceKey,
            ElementInstanceKey = owner.Key,
            DefinitionKey = State.DefinitionKey,
            ElementId = elementId,
            Kind = kind,
            MessageName = messageName,
            CorrelationKey = correlationKey,
            IsSignal = isSignal,
            TenantId = State.TenantId,
        };
        State.Subscriptions[subscription.Key] = subscription;
        Emit(new SubscriptionChanged(subscription.Copy()));
        return subscription;
    }

    /// <summary>Removes every timer and subscription owned by the element (it completed or was terminated).</summary>
    public void CloseTriggersOf(ElementInstance owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        foreach (var timer in State.Timers.Values.Where(t => t.ElementInstanceKey == owner.Key && t.IsActive))
        {
            DeactivateTimer(timer);
        }

        foreach (var subscription in State.Subscriptions.Values.Where(s => s.ElementInstanceKey == owner.Key).ToList())
        {
            CloseSubscription(subscription);
        }
    }

    public void DeactivateTimer(TimerRecord timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        timer.IsActive = false;
        Emit(new TimerChanged(timer.Copy()));
    }

    public void CloseSubscription(SubscriptionRecord subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        State.Subscriptions.Remove(subscription.Key);
        Emit(new SubscriptionClosed(subscription.Key));
    }

    public void TakeFlows(ElementInstance from, IEnumerable<SequenceFlow> flows)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(flows);
        foreach (var flow in flows)
        {
            Enqueue(new ActivateElement(flow.TargetRef, from.ScopeKey, flow.Id));
        }
    }

    public void TakeAllOutgoing(ElementInstance from)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (from.LoopCounter is not null)
        {
            MultiInstance.ChildCompleted(this, from); // a multi-instance child reports to its body instead of taking flows (FR-EXEC-012)
            return;
        }

        TakeFlows(from, State.Graph.Outgoing(from.ElementId));
    }

    /// <summary>The instance itself acts as owner of triggers that belong to the root scope (event sub-processes).</summary>
    public ElementInstance RootOwner => new()
    {
        Key = State.InstanceKey,
        ElementId = State.Graph.Process.Id ?? string.Empty,
        ElementType = "process",
        ScopeKey = State.InstanceKey,
        State = ElementState.Active,
        StartedAt = State.StartedAt,
    };

    /// <summary>Writes an engine-internal variable (reserved prefix) without the user-facing checks.</summary>
    public void WriteInternal(long scope, string name, JsonNode? value)
    {
        State.Variables.SetIn(scope, name, value);
        Emit(new VariableWritten(scope, name, value?.DeepClone()));
    }

    /// <summary>Closes the timers and subscriptions owned by a key that is not an element instance (the root scope).</summary>
    public void CloseTriggersOwnedBy(long ownerKey)
    {
        foreach (var timer in State.Timers.Values.Where(t => t.ElementInstanceKey == ownerKey && t.IsActive))
        {
            DeactivateTimer(timer);
        }

        foreach (var subscription in State.Subscriptions.Values.Where(s => s.ElementInstanceKey == ownerKey).ToList())
        {
            CloseSubscription(subscription);
        }
    }

    /// <summary>
    /// Removes an activity's local variables when it completes. A multi-instance child keeps them until its body has read the
    /// output element (FR-EXEC-012).
    /// </summary>
    public void ClearLocalScope(ElementInstance instance)
    {
        if (instance.LoopCounter is null)
        {
            ClearLocalScopeNow(instance);
        }
    }

    public void ClearLocalScopeNow(ElementInstance instance)
    {
        if (State.Variables.RemoveScope(instance.Key).Count > 0)
        {
            Emit(new ScopeVariablesCleared(instance.Key));
        }
    }

    public void CancelJobsOf(ElementInstance instance)
    {
        foreach (var job in State.Jobs.Values.Where(j => j.ElementInstanceKey == instance.Key && j.State is JobState.Activatable or JobState.Activated))
        {
            job.State = JobState.Canceled;
            job.Worker = null;
            job.Deadline = null;
            Emit(new JobChanged(job.Copy()));
            Record(HistoryEventTypes.JobCanceled, instance, new JsonObject { ["jobKey"] = job.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    public void CancelUserTasksOf(ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        foreach (var task in State.UserTasks.Values.Where(t => t.ElementInstanceKey == instance.Key && t.State == UserTaskState.Created))
        {
            task.State = UserTaskState.Canceled;
            Emit(new UserTaskChanged(task.Copy()));
        }
    }

    public IncidentRecord RaiseIncident(string type, string message, ElementInstance? element, long? jobKey = null)
    {
        var incident = new IncidentRecord
        {
            Key = NextKey(),
            InstanceKey = State.InstanceKey,
            ElementInstanceKey = element?.Key,
            JobKey = jobKey,
            ErrorType = type,
            ErrorMessage = message,
            State = IncidentState.Open,
            CreatedAt = Now,
        };
        State.Incidents[incident.Key] = incident;
        _raisedIncidents.Add(incident.Key);
        Emit(new IncidentChanged(incident.Copy()));
        Record(HistoryEventTypes.IncidentCreated, element, new JsonObject { ["errorType"] = type, ["message"] = message });
        return incident;
    }

    /// <summary>Evaluates against the variables visible from <paramref name="scope"/>, optionally overlaid with extra variables.</summary>
    public EvaluationResult Evaluate(string expression, long scope, JsonObject? overlay = null)
    {
        var visible = State.Variables.Visible(scope);
        if (overlay is not null)
        {
            foreach (var (name, value) in overlay)
            {
                visible[name] = value?.DeepClone();
            }
        }

        return FeelEvaluator.Evaluate(expression, new ExpressionContext(ExpressionJson.ToVariables(visible), Now));
    }

    /// <summary>
    /// Writes a variable, enforcing the reserved prefix and size limits (FR-VAR-002, FR-VAR-003).
    /// With <paramref name="propagate"/> the write goes to the scope that already defines the name (FR-VAR-001).
    /// </summary>
    public VariableWriteError? WriteVariable(long scope, string name, JsonNode? value, bool propagate)
    {
        if (name.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            return new VariableWriteError(IncidentTypes.IoMappingError, $"Variable name '{name}' uses the reserved prefix '{ReservedPrefix}'.");
        }

        var size = VariableStore.SizeOf(value);
        if (size > Options.MaxVariableBytes || State.Variables.TotalBytes + size > Options.MaxInstanceVariableBytes)
        {
            return new VariableWriteError(IncidentTypes.VariableSizeExceeded, $"Variable '{name}' exceeds the size limits.");
        }

        var target = propagate ? State.Variables.Propagate(scope, name, value) : Assign(scope, name, value);
        Emit(new VariableWritten(target, name, value?.DeepClone()));
        Record(HistoryEventTypes.VariableSet, null, new JsonObject { ["name"] = name, ["scope"] = target.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return null;
    }

    private long Assign(long scope, string name, JsonNode? value)
    {
        State.Variables.SetIn(scope, name, value);
        return scope;
    }
}
