using System.Text.Json.Nodes;
using Flowd.Engine.Results;
using Flowd.Engine.State;

namespace Flowd.Engine.Effects;

/// <summary>History event types (FR-HIS-001, CC-012).</summary>
public static class HistoryEventTypes
{
    public const string InstanceStarted = "INSTANCE_STARTED";
    public const string InstanceCompleted = "INSTANCE_COMPLETED";
    public const string InstanceTerminated = "INSTANCE_TERMINATED";
    public const string ElementActivating = "ELEMENT_ACTIVATING";
    public const string ElementActive = "ELEMENT_ACTIVE";
    public const string ElementCompleting = "ELEMENT_COMPLETING";
    public const string ElementCompleted = "ELEMENT_COMPLETED";
    public const string ElementTerminating = "ELEMENT_TERMINATING";
    public const string ElementTerminated = "ELEMENT_TERMINATED";
    public const string VariableSet = "VARIABLE_SET";
    public const string JobCreated = "JOB_CREATED";
    public const string JobActivated = "JOB_ACTIVATED";
    public const string JobCompleted = "JOB_COMPLETED";
    public const string JobFailed = "JOB_FAILED";
    public const string JobCanceled = "JOB_CANCELED";
    public const string JobErrorThrown = "JOB_ERROR_THROWN";
    public const string JobRetriesUpdated = "JOB_RETRIES_UPDATED";
    public const string IncidentCreated = "INCIDENT_CREATED";
    public const string IncidentResolved = "INCIDENT_RESOLVED";
    public const string JoinTokensAbandoned = "JOIN_TOKENS_ABANDONED";
}

/// <summary>The instance row was created (first effect of <c>StartInstance</c>).</summary>
public sealed record InstanceStarted(long InstanceKey, long DefinitionKey, string TenantId, string? BusinessKey, DateTimeOffset StartedAt);

/// <summary>The instance changed state.</summary>
public sealed record InstanceStateChanged(InstanceState State, DateTimeOffset? EndedAt);

/// <summary>Insert or update of an element instance (snapshot).</summary>
public sealed record ElementChanged(ElementInstance Snapshot);

/// <summary>Insert or update of a job (snapshot).</summary>
public sealed record JobChanged(JobRecord Snapshot);

/// <summary>Insert or update of an incident (snapshot).</summary>
public sealed record IncidentChanged(IncidentRecord Snapshot);

/// <summary>A variable was written into a scope.</summary>
public sealed record VariableWritten(long Scope, string Name, JsonNode? Value);

/// <summary>All variables of a scope were removed (an activity's local scope ends with it).</summary>
public sealed record ScopeVariablesCleared(long Scope);

/// <summary>An audit-trail entry (FR-HIS-001).</summary>
public sealed record HistoryAppended(string EventType, long? ElementInstanceKey, string? ElementId, JsonObject? Payload, DateTimeOffset OccurredAt);

/// <summary>Everything a command changes; stores apply these in one transaction (FR-ARCH-002, UNI-002).</summary>
public union EngineEffect(
    InstanceStarted,
    InstanceStateChanged,
    ElementChanged,
    JobChanged,
    IncidentChanged,
    VariableWritten,
    ScopeVariablesCleared,
    HistoryAppended);

/// <summary>Where the instance stands after a command (UNI-002).</summary>
public sealed record Continued;

public sealed record Waiting;

public sealed record Completed;

public sealed record IncidentRaised(IReadOnlyList<long> IncidentKeys);

public sealed record Terminated;

public union StepOutcome(Continued, Waiting, Completed, IncidentRaised, Terminated);

/// <summary>The command was processed; <see cref="Effects"/> must be persisted atomically.</summary>
public sealed record CommandAccepted(IReadOnlyList<EngineEffect> Effects, StepOutcome Outcome);

/// <summary>The command was refused and nothing changed.</summary>
public sealed record CommandRejected(Failure Failure);

public union CommandOutcome(CommandAccepted, CommandRejected);

/// <summary>Result of looking at a job for completion (UNI-002).</summary>
public sealed record JobCompletable;

public sealed record JobAlreadyCompleted;

public sealed record JobNotActivated;

public sealed record JobStale;

public union JobCompletionOutcome(JobCompletable, JobAlreadyCompleted, JobNotActivated, JobStale);
