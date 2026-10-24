using System.Text.Json.Nodes;

namespace Flowd.Contracts.Execution;

public sealed record StartInstanceRequestDto(string BpmnProcessId, int? Version, JsonObject? Variables, string? BusinessKey, string? StartElementId);

public sealed record StartInstanceResponseDto(string ProcessInstanceKey, string ProcessDefinitionKey, int Version, string BpmnProcessId);

public sealed record InstanceDto(
    string Key,
    string ProcessDefinitionKey,
    string BpmnProcessId,
    int Version,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? BusinessKey);

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
