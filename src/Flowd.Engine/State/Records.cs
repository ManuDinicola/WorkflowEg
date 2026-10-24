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
