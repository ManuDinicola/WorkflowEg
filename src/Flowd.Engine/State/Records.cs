using System.Text.Json.Nodes;

namespace Flowd.Engine.State;

public enum ElementState
{
    Activating,
    Active,
    Completing,
    Completed,
    Terminating,
    Terminated,
}

public enum InstanceState
{
    Active,
    Completed,
    Terminated,
}

public enum JobState
{
    Activatable,
    Activated,
    Completed,
    Failed,
    Canceled,
    ErrorThrown,
}

public enum IncidentState
{
    Open,
    Resolved,
}

/// <summary>Incident types of section 12.1 (CC-012).</summary>
public static class IncidentTypes
{
    public const string JobNoRetries = "JOB_NO_RETRIES";
    public const string ConditionError = "CONDITION_ERROR";
    public const string ExtractValueError = "EXTRACT_VALUE_ERROR";
    public const string IoMappingError = "IO_MAPPING_ERROR";
    public const string CalledElementError = "CALLED_ELEMENT_ERROR";
    public const string UnhandledErrorEvent = "UNHANDLED_ERROR_EVENT";
    public const string VariableSizeExceeded = "VARIABLE_SIZE_EXCEEDED";
    public const string LoopLimitExceeded = "LOOP_LIMIT_EXCEEDED";
}

/// <summary>A token / activity instance (section 5.1).</summary>
public sealed class ElementInstance
{
    public required long Key { get; init; }

    public required string ElementId { get; init; }

    public required string ElementType { get; init; }

    /// <summary>The scope this element lives in. Its own local variable scope is its <see cref="Key"/>.</summary>
    public required long ScopeKey { get; init; }

    public ElementState State { get; set; }

    /// <summary>The sequence flow the token arrived on; used by parallel joins.</summary>
    public string? IncomingFlowId { get; init; }

    /// <summary>Iteration index of a multi-instance child (FR-EXEC-012); null otherwise.</summary>
    public int? LoopCounter { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; set; }

    public bool IsLive => State is not (ElementState.Completed or ElementState.Terminated);

    public ElementInstance Copy() => (ElementInstance)MemberwiseClone();
}

/// <summary>A unit of work for an external worker (section 5.1).</summary>
public sealed class JobRecord
{
    public required long Key { get; init; }

    public required long InstanceKey { get; init; }

    public required long ElementInstanceKey { get; init; }

    public required string ElementId { get; init; }

    public required string Type { get; init; }

    public JobState State { get; set; }

    public int Retries { get; set; }

    public DateTimeOffset? RetryBackoffUntil { get; set; }

    public DateTimeOffset? Deadline { get; set; }

    public string? Worker { get; set; }

    public IReadOnlyDictionary<string, string> CustomHeaders { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Snapshot of the variables visible to the task after input mappings (FR-JOB-008).</summary>
    public JsonObject Variables { get; init; } = [];

    /// <summary>Variables the worker returned on completion; kept so output mappings can be re-run (DECISIONS.md).</summary>
    public JsonObject? ResultVariables { get; set; }

    public string? ErrorMessage { get; set; }

    public string TenantId { get; init; } = string.Empty;

    public required DateTimeOffset CreatedAt { get; init; }

    public JobRecord Copy() => (JobRecord)MemberwiseClone();
}

/// <summary>A failure that needs operator action (section 12).</summary>
public sealed class IncidentRecord
{
    public required long Key { get; init; }

    public required long InstanceKey { get; init; }

    public long? ElementInstanceKey { get; init; }

    public long? JobKey { get; init; }

    public required string ErrorType { get; init; }

    public required string ErrorMessage { get; init; }

    public IncidentState State { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public IncidentRecord Copy() => (IncidentRecord)MemberwiseClone();
}

/// <summary>What a timer or message subscription is attached to (section 5.1).</summary>
public enum TriggerKind
{
    Catch,
    Boundary,
    EventGateway,
    EventSubProcess,
    Start,
}

/// <summary>A pending timer. <see cref="ElementInstanceKey"/> is the owner: the waiting event, the attached activity or the event gateway.</summary>
public sealed class TimerRecord
{
    public required long Key { get; init; }

    public long? InstanceKey { get; init; }

    public long? ElementInstanceKey { get; init; }

    public long? DefinitionKey { get; init; }

    /// <summary>The element to continue with when the timer fires (the boundary or catch event).</summary>
    public required string ElementId { get; init; }

    public required TriggerKind Kind { get; init; }

    public DateTimeOffset DueAt { get; set; }

    /// <summary>Remaining firings of a cycle; null for one-shot timers and endless cycles.</summary>
    public int? Repetitions { get; set; }

    /// <summary>Set for cycles: the next due time is the previous one plus this interval (FR-TMR-006).</summary>
    public TimeSpan? Interval { get; init; }

    public bool IsActive { get; set; } = true;

    public string TenantId { get; init; } = string.Empty;

    public TimerRecord Copy() => (TimerRecord)MemberwiseClone();
}

/// <summary>A process waiting for a message with a name and correlation key (section 11).</summary>
public sealed class SubscriptionRecord
{
    public required long Key { get; init; }

    public long? InstanceKey { get; init; }

    public long? ElementInstanceKey { get; init; }

    public required long DefinitionKey { get; init; }

    public required string ElementId { get; init; }

    public required TriggerKind Kind { get; init; }

    public required string MessageName { get; init; }

    public required string CorrelationKey { get; init; }

    /// <summary>True for a signal subscription: signals reach every subscriber and are never buffered (FR-SIG-001).</summary>
    public bool IsSignal { get; init; }

    public string TenantId { get; init; } = string.Empty;

    public SubscriptionRecord Copy() => (SubscriptionRecord)MemberwiseClone();
}

public enum UserTaskState
{
    Created,
    Completed,
    Canceled,
}

/// <summary>A native user task (section 5.1).</summary>
public sealed class UserTaskRecord
{
    public required long Key { get; init; }

    public required long InstanceKey { get; init; }

    public required long ElementInstanceKey { get; init; }

    public required string ElementId { get; init; }

    public string? Assignee { get; set; }

    public IReadOnlyList<string> CandidateGroups { get; init; } = [];

    public IReadOnlyList<string> CandidateUsers { get; init; } = [];

    public string? DueDate { get; init; }

    public string? FormKey { get; init; }

    public UserTaskState State { get; set; }

    public System.Text.Json.Nodes.JsonObject? ResultVariables { get; set; }

    public string TenantId { get; init; } = string.Empty;

    public required DateTimeOffset CreatedAt { get; init; }

    public UserTaskRecord Copy() => (UserTaskRecord)MemberwiseClone();
}
