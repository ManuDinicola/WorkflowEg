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

/// <summary>A broadcast signal reaches a waiting subscription (FR-SIG-001).</summary>
public sealed record TriggerSignal(long SubscriptionKey, string SignalName, JsonObject? Variables);

/// <summary>A user claims a task; fails when someone else holds it (API-011).</summary>
public sealed record ClaimUserTask(long TaskKey, string User);

public sealed record UnclaimUserTask(long TaskKey);

public sealed record AssignUserTask(long TaskKey, string User);

/// <summary>Completes a user task with result variables (API-011).</summary>
public sealed record CompleteUserTask(long TaskKey, JsonObject? Variables, string? User);

/// <summary>The child instance of a call activity completed (FR-EXEC-011).</summary>
public sealed record CompleteCallActivity(long ElementInstanceKey, JsonObject Variables);

/// <summary>The child instance could not be started or was terminated: incident CALLED_ELEMENT_ERROR.</summary>
public sealed record FailCallActivity(long ElementInstanceKey, string Message);

/// <summary>An error left the child instance unhandled; look for a handler at the call activity.</summary>
public sealed record ThrowFromCallActivity(long ElementInstanceKey, string ErrorCode);

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
    CorrelateMessage,
    TriggerSignal,
    ClaimUserTask,
    UnclaimUserTask,
    AssignUserTask,
    CompleteUserTask,
    CompleteCallActivity,
    FailCallActivity,
    ThrowFromCallActivity);
