using Flowd.Bpmn.Model;

namespace Flowd.Engine.State;

/// <summary>Static lookups over a process: nodes by id and flows in document order.</summary>
public sealed class ProcessGraph
{
    private readonly Dictionary<string, FlowNode> _nodes;
    private readonly ILookup<string, SequenceFlow> _outgoing;
    private readonly ILookup<string, SequenceFlow> _incoming;

    public ProcessGraph(BpmnProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        Process = process;
        _nodes = process.AllNodes().ToDictionary(n => n.Id, StringComparer.Ordinal);
        _outgoing = process.AllFlows().ToLookup(f => f.SourceRef, StringComparer.Ordinal);
        _incoming = process.AllFlows().ToLookup(f => f.TargetRef, StringComparer.Ordinal);
    }

    public BpmnProcess Process { get; }

    public FlowNode Node(string id) =>
        _nodes.TryGetValue(id, out var node) ? node : throw new InvalidOperationException($"Unknown element '{id}'.");

    public IReadOnlyList<SequenceFlow> Outgoing(string nodeId) => [.. _outgoing[nodeId]];

    public IReadOnlyList<SequenceFlow> Incoming(string nodeId) => [.. _incoming[nodeId]];

    /// <summary>Boundary events attached to an activity, in document order.</summary>
    public IReadOnlyList<BoundaryEvent> BoundaryEventsOf(string activityId) =>
        [.. Process.AllNodes().OfType<BoundaryEvent>().Where(b => string.Equals(b.AttachedToRef, activityId, StringComparison.Ordinal))];

    /// <summary>Event sub-processes declared directly in a scope: the process root (<c>null</c>) or one sub-process.</summary>
    public IReadOnlyList<SubProcess> EventSubProcessesIn(string? scopeNodeId)
    {
        var nodes = scopeNodeId is null ? Process.Nodes : (Node(scopeNodeId) as SubProcess)?.Nodes ?? [];
        return [.. nodes.OfType<SubProcess>().Where(s => s.IsEventSubProcess)];
    }

    /// <summary>The link catch event named <paramref name="linkName"/> in the same scope as <paramref name="throwId"/>, or null.</summary>
    public IntermediateCatchEvent? LinkCatch(string throwId, string linkName) =>
        ScopeOf(Process.Nodes, throwId)?.OfType<IntermediateCatchEvent>().FirstOrDefault(c => c.Definition is LinkEventDefinition l && string.Equals(l.Name, linkName, StringComparison.Ordinal));

    /// <summary>The id of the embedded sub-process that directly contains <paramref name="nodeId"/>, or null for the process root.</summary>
    public string? ParentOf(string nodeId) => ParentOf(Process.Nodes, null, nodeId);

    private static string? ParentOf(IReadOnlyList<FlowNode> nodes, string? parent, string nodeId)
    {
        if (nodes.Any(n => string.Equals(n.Id, nodeId, StringComparison.Ordinal)))
        {
            return parent;
        }

        return nodes.OfType<SubProcess>().Select(s => ParentOf(s.Nodes, s.Id, nodeId)).FirstOrDefault(p => p is not null);
    }

    private static IReadOnlyList<FlowNode>? ScopeOf(IReadOnlyList<FlowNode> nodes, string id)
    {
        if (nodes.Any(n => string.Equals(n.Id, id, StringComparison.Ordinal)))
        {
            return nodes;
        }

        return nodes.OfType<SubProcess>().Select(s => ScopeOf(s.Nodes, id)).FirstOrDefault(s => s is not null);
    }

    public SequenceFlow Flow(string id) =>
        Process.AllFlows().FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Unknown flow '{id}'.");
}

/// <summary>Everything the interpreter knows about one process instance. Mutated in place by <c>Interpreter</c> (FR-ARCH-001).</summary>
public sealed class EngineState
{
    public EngineState(long instanceKey, long definitionKey, ProcessGraph graph, string tenantId, DateTimeOffset startedAt)
    {
        InstanceKey = instanceKey;
        DefinitionKey = definitionKey;
        Graph = graph;
        TenantId = tenantId;
        StartedAt = startedAt;
        Variables = new VariableStore(ParentScopeOf);
    }

    public long InstanceKey { get; }

    public long DefinitionKey { get; }

    public ProcessGraph Graph { get; }

    public string TenantId { get; }

    public string? BusinessKey { get; set; }

    /// <summary>Set when this instance was started by a call activity (FR-EXEC-011).</summary>
    public long? ParentInstanceKey { get; init; }

    public long? ParentElementInstanceKey { get; init; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? EndedAt { get; set; }

    public InstanceState State { get; set; } = InstanceState.Active;

    public VariableStore Variables { get; private set; }

    public Dictionary<long, ElementInstance> Elements { get; } = [];

    public Dictionary<long, JobRecord> Jobs { get; } = [];

    public Dictionary<long, IncidentRecord> Incidents { get; } = [];

    public Dictionary<long, TimerRecord> Timers { get; } = [];

    public Dictionary<long, SubscriptionRecord> Subscriptions { get; } = [];

    public Dictionary<long, UserTaskRecord> UserTasks { get; } = [];

    /// <summary>The root scope of an instance is the instance key itself; an element's local scope is its own key.</summary>
    public long? ParentScopeOf(long scope)
    {
        if (scope == InstanceKey)
        {
            return null;
        }

        return Elements.TryGetValue(scope, out var element) ? element.ScopeKey : null;
    }

    public IEnumerable<ElementInstance> LiveElements() => Elements.Values.Where(e => e.IsLive).OrderBy(e => e.Key);

    /// <summary>Deep copy; the in-memory test store and determinism tests rely on it.</summary>
    public EngineState Clone()
    {
        var copy = new EngineState(InstanceKey, DefinitionKey, Graph, TenantId, StartedAt)
        {
            BusinessKey = BusinessKey,
            ParentInstanceKey = ParentInstanceKey,
            ParentElementInstanceKey = ParentElementInstanceKey,
            EndedAt = EndedAt,
            State = State,
        };
        foreach (var (key, element) in Elements)
        {
            copy.Elements[key] = element.Copy();
        }

        foreach (var (key, job) in Jobs)
        {
            copy.Jobs[key] = job.Copy();
        }

        foreach (var (key, incident) in Incidents)
        {
            copy.Incidents[key] = incident.Copy();
        }

        foreach (var (key, timer) in Timers)
        {
            copy.Timers[key] = timer.Copy();
        }

        foreach (var (key, subscription) in Subscriptions)
        {
            copy.Subscriptions[key] = subscription.Copy();
        }

        foreach (var (key, task) in UserTasks)
        {
            copy.UserTasks[key] = task.Copy();
        }

        copy.Variables = Variables.CloneWith(copy.ParentScopeOf);
        return copy;
    }
}
