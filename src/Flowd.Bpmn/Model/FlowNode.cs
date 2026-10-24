namespace Flowd.Bpmn.Model;

/// <summary>Root of the closed BPMN element hierarchy; only this assembly may derive from it (UNI-004).</summary>
public closed class FlowNode
{
    public required string Id { get; init; }

    public string? Name { get; init; }
}

/// <summary>None start event (message, timer and signal variants arrive in M4/M5).</summary>
public sealed class StartEvent : FlowNode;

/// <summary>None or terminate end event (FR-EXEC-008).</summary>
public sealed class EndEvent : FlowNode
{
    public bool IsTerminate { get; init; }
}

/// <summary>Job-worker service task (FR-EXEC-006).</summary>
public sealed class ServiceTask : FlowNode
{
    /// <summary>Job type from <c>flowd:taskDefinition type</c>; null when missing (rejected by the validator).</summary>
    public string? TaskType { get; init; }

    /// <summary>Raw <c>retries</c> attribute: a literal or an expression starting with <c>=</c>.</summary>
    public string? Retries { get; init; }
}

/// <summary>Pass-through manual task.</summary>
public sealed class ManualTask : FlowNode;

/// <summary>Exclusive gateway (FR-EXEC-003).</summary>
public sealed class ExclusiveGateway : FlowNode
{
    public string? DefaultFlowId { get; init; }
}

/// <summary>Parallel gateway (FR-EXEC-004).</summary>
public sealed class ParallelGateway : FlowNode;

/// <summary>An element this version cannot run. Kept in the model so the validator can report it precisely (section 1.3).</summary>
public sealed class UnsupportedNode : FlowNode
{
    public required string ElementType { get; init; }
}
