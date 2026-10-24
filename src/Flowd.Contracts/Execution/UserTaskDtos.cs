using System.Text.Json.Nodes;

namespace Flowd.Contracts.Execution;

public sealed record BroadcastSignalRequestDto(string Name, JsonObject? Variables);

public sealed record BroadcastSignalResponseDto(int Triggered);

public sealed record UserTaskDto(
    string Key,
    string ProcessInstanceKey,
    string ElementId,
    string? Assignee,
    IReadOnlyList<string> CandidateGroups,
    IReadOnlyList<string> CandidateUsers,
    string? DueDate,
    string? FormKey,
    string State,
    DateTimeOffset CreatedAt);

public sealed record ClaimUserTaskRequestDto(string User);

public sealed record AssignUserTaskRequestDto(string User);

public sealed record CompleteUserTaskRequestDto(JsonObject? Variables, string? User);
