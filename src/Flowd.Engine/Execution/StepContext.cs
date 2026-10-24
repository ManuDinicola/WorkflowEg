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

    internal void ClearQueue() => _queue.Clear();

    public void Emit(EngineEffect effect) => _effects.Add(effect);

    public void Record(string eventType, ElementInstance? element = null, JsonObject? payload = null) =>
        Emit(new HistoryAppended(eventType, element?.Key, element?.ElementId, payload, Now));

    public ElementInstance CreateElement(FlowNode node, long scopeKey, string? incomingFlowId)
    {
        ArgumentNullException.ThrowIfNull(node);
        var instance = new ElementInstance
        {
            Key = NextKey(),
            ElementId = node.Id,
            ElementType = ElementTypes.NameOf(node),
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
    }

    public void Terminate(ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Record(HistoryEventTypes.ElementTerminating, instance);
        instance.State = ElementState.Terminated;
        instance.EndedAt = Now;
        Emit(new ElementChanged(instance.Copy()));
        Record(HistoryEventTypes.ElementTerminated, instance);
        ClearLocalScope(instance);
        CancelJobsOf(instance);
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

    public void TakeAllOutgoing(ElementInstance from) =>
        TakeFlows(from, State.Graph.Outgoing(from.ElementId));

    public void ClearLocalScope(ElementInstance instance)
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
