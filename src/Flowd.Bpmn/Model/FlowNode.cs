namespace Flowd.Bpmn.Model;

/// <summary>Root of the closed BPMN element hierarchy; only this assembly may derive from it (UNI-004).</summary>
public closed class FlowNode
{
    public required string Id { get; init; }

    public string? Name { get; init; }
}

/// <summary>None start event (message, timer and signal variants arrive in M4/M5).</summary>
public sealed class StartEvent : FlowNode
{
    /// <summary>Null for a none start event; a timer or message definition starts instances from outside (FR-TMR-005, FR-MSG-004).</summary>
    public EventDefinition? Definition { get; init; }
}

/// <summary>None or terminate end event (FR-EXEC-008).</summary>
public sealed class EndEvent : FlowNode
{
    public bool IsTerminate { get; init; }
}

/// <summary>Job-worker service task (FR-EXEC-006).</summary>
public sealed class ServiceTask : FlowNode
{
    /// <summary>The BPMN element this was read from: serviceTask, sendTask or businessRuleTask (all worker-based).</summary>
    public string BpmnType { get; init; } = "serviceTask";

    /// <summary>Job type from <c>flowd:taskDefinition type</c>; null when missing (rejected by the validator).</summary>
    public string? TaskType { get; init; }

    /// <summary>Raw <c>retries</c> attribute: a literal or an expression starting with <c>=</c>.</summary>
    public string? Retries { get; init; }

    /// <summary>Input mappings: evaluated on activation into the task-local scope (section 8.4).</summary>
    public IReadOnlyList<IoMapping> Inputs { get; init; } = [];

    /// <summary>Output mappings: evaluated on completion and written to the parent scope (section 8.4).</summary>
    public IReadOnlyList<IoMapping> Outputs { get; init; } = [];

    /// <summary>When no output mapping exists, all result variables propagate unless <c>flowd:propagateAll="false"</c>.</summary>
    public bool PropagateAllOutputs { get; init; } = true;

    /// <summary>Static headers from <c>flowd:taskHeaders</c> returned to workers (FR-JOB-009).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
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

/// <summary>Embedded sub-process: a scope with its own start event, flows and variables (FR-EXEC-010).</summary>
public sealed class SubProcess : FlowNode
{
    public IReadOnlyList<FlowNode> Nodes { get; init; } = [];

    public IReadOnlyList<SequenceFlow> Flows { get; init; } = [];

    /// <summary>Evaluated on activation into the sub-process scope.</summary>
    public IReadOnlyList<IoMapping> Inputs { get; init; } = [];

    /// <summary>Evaluated on completion against the sub-process scope and written to the parent scope.</summary>
    public IReadOnlyList<IoMapping> Outputs { get; init; } = [];
}

/// <summary>Inclusive gateway (FR-EXEC-005).</summary>
public sealed class InclusiveGateway : FlowNode
{
    public string? DefaultFlowId { get; init; }
}

/// <summary>Script task: evaluates an expression and stores the result (FR-EXEC-007). No script engine is involved.</summary>
public sealed class ScriptTask : FlowNode
{
    public string? Expression { get; init; }

    public string? ResultVariable { get; init; }
}

/// <summary>How a timer event computes its due time (FR-TMR-001).</summary>
public enum TimerKind
{
    Date,
    Duration,
    Cycle,
}

/// <summary>What triggers an event: closed so every consumer handles every kind (UNI-004).</summary>
public closed class EventDefinition;

/// <summary>Timer event; <see cref="Expression"/> is a literal ISO 8601 value or a <c>=</c> expression.</summary>
public sealed class TimerEventDefinition : EventDefinition
{
    public required TimerKind Kind { get; init; }

    public required string Expression { get; init; }
}

/// <summary>Message event; the correlation key is an expression evaluated when the event is activated (FR-MSG-005).</summary>
public sealed class MessageEventDefinition : EventDefinition
{
    public required string Name { get; init; }

    public string? CorrelationKey { get; init; }
}

/// <summary>Intermediate catch event: waits for a timer or a message (section 6.1).</summary>
public sealed class IntermediateCatchEvent : FlowNode
{
    public required EventDefinition Definition { get; init; }
}

/// <summary>Boundary event attached to an activity; interrupting unless <see cref="CancelActivity"/> is false (FR-EXEC-013).</summary>
public sealed class BoundaryEvent : FlowNode
{
    public required string AttachedToRef { get; init; }

    public bool CancelActivity { get; init; } = true;

    public required EventDefinition Definition { get; init; }
}

/// <summary>Receive task: waits for a message, can carry boundary events.</summary>
public sealed class ReceiveTask : FlowNode
{
    public required MessageEventDefinition Message { get; init; }
}

/// <summary>Event-based gateway: the first of its catch-event targets to trigger wins (FR-EXEC-014).</summary>
public sealed class EventBasedGateway : FlowNode;
