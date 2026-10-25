using System.Text.Json.Nodes;

namespace Flowd.Contracts.Execution;

/// <summary>With <c>AwaitResult</c> the call waits (up to <c>RequestTimeoutMs</c>, at most 60 s) for the instance to end and returns its state and variables (FR-API-021).</summary>
public sealed record StartInstanceRequestDto(string BpmnProcessId, int? Version, JsonObject? Variables, string? BusinessKey, string? StartElementId, bool? AwaitResult = null, long? RequestTimeoutMs = null, IReadOnlyList<string>? FetchVariables = null, IReadOnlyList<string>? Tags = null);

/// <summary>State and Variables are set only when the request asked to await the result; Variables only when the instance completed.</summary>
public sealed record StartInstanceResponseDto(string ProcessInstanceKey, string ProcessDefinitionKey, int Version, string BpmnProcessId, string? State = null, JsonObject? Variables = null);

public sealed record InstanceDto(
    string Key,
    string ProcessDefinitionKey,
    string BpmnProcessId,
    int Version,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? BusinessKey,
    IReadOnlyList<string>? Tags = null);

public sealed record SetVariablesRequestDto(JsonObject Variables, string? ScopeKey, bool Local);

public sealed record HistoryEventDto(string Seq, string EventType, string? ElementId, string? ElementInstanceKey, JsonObject Payload, DateTimeOffset OccurredAt);

public sealed record IncidentDto(
    string Key,
    string ProcessInstanceKey,
    string? ElementInstanceKey,
    string? JobKey,
    string ErrorType,
    string ErrorMessage,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record ActivateJobsRequestDto(string Type, string Worker, long TimeoutMs, int MaxJobs, long? RequestTimeoutMs, IReadOnlyList<string>? FetchVariables);

public sealed record JobDto(
    string Key,
    string Type,
    string ProcessInstanceKey,
    string ElementId,
    int Retries,
    DateTimeOffset Deadline,
    JsonObject Variables,
    IReadOnlyDictionary<string, string> CustomHeaders);

public sealed record ActivateJobsResponseDto(IReadOnlyList<JobDto> Jobs);

public sealed record CompleteJobRequestDto(JsonObject? Variables, string? Worker);

public sealed record FailJobRequestDto(int Retries, string? ErrorMessage, long? RetryBackoffMs, string? Worker);

public sealed record ThrowJobErrorRequestDto(string ErrorCode, string? ErrorMessage, JsonObject? Variables, string? Worker);

public sealed record UpdateJobRequestDto(int? Retries, long? TimeoutMs);

/// <summary>Moves tokens of a running instance: ends the given element instances and starts the given elements (FR-OPS-001).</summary>
public sealed record ModifyInstanceRequestDto(IReadOnlyList<string>? TerminateElementInstanceKeys, IReadOnlyList<string>? ActivateElementIds, JsonObject? Variables);

/// <summary>A running element instance of a process instance.</summary>
public sealed record ElementInstanceDto(string Key, string ElementId, string ElementType, string State, string ScopeKey);

/// <summary>Moves a running instance to another version of its process; <c>mapping</c> renames elements that changed id (FR-OPS-004).</summary>
public sealed record MigrateInstanceRequestDto(string TargetDefinitionKey, IReadOnlyDictionary<string, string>? Mapping);
