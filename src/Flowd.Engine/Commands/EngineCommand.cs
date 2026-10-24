using System.Text.Json.Nodes;

namespace Flowd.Engine.Commands;

/// <summary>Starts the instance (FR-EXEC-001). <see cref="StartElementId"/> selects one of several start events.</summary>
public sealed record StartInstance(JsonObject? Variables, string? StartElementId);

/// <summary>Worker finished a job (FR-JOB-002).</summary>
public sealed record CompleteJob(long JobKey, JsonObject? Variables, string? Worker);

/// <summary>Worker failed a job (FR-JOB-003).</summary>
public sealed record FailJob(long JobKey, int Retries, string? ErrorMessage, TimeSpan? RetryBackoff, string? Worker);

/// <summary>Worker throws a BPMN error (FR-JOB-005).</summary>
public sealed record ThrowJobError(long JobKey, string ErrorCode, string? ErrorMessage, JsonObject? Variables, string? Worker);

/// <summary>Operator changes the remaining retries of a job (FR-JOB-006).</summary>
public sealed record UpdateJobRetries(long JobKey, int Retries);

/// <summary>Terminates the instance (FR-EXEC-015).</summary>
public sealed record CancelInstance;

/// <summary>Sets variables from outside (API-006).</summary>
public sealed record SetVariables(JsonObject Variables, long? ScopeKey, bool Local);

/// <summary>Re-executes the step that raised an incident (FR-INC-002).</summary>
public sealed record ResolveIncident(long IncidentKey);

/// <summary>A due timer fires (FR-TMR-002).</summary>
public sealed record TriggerTimer(long TimerKey);

/// <summary>A message reaches a waiting subscription (FR-MSG-002).</summary>
public sealed record CorrelateMessage(long SubscriptionKey, long MessageKey, string MessageName, JsonObject? Variables);

/// <summary>Every intent the engine can be asked to process (section 7, UNI-002).</summary>
public union EngineCommand(
    StartInstance,
    CompleteJob,
    FailJob,
    ThrowJobError,
    UpdateJobRetries,
    CancelInstance,
    SetVariables,
    ResolveIncident,
    TriggerTimer,
    CorrelateMessage);
