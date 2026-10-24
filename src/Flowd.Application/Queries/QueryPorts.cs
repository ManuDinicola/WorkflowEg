using System.Text.Json.Nodes;

namespace Flowd.Application.Queries;

public sealed record InstanceView(
    long Key,
    long DefinitionKey,
    string BpmnProcessId,
    int Version,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? BusinessKey);

public sealed record InstanceFilter(string? State, string? BpmnProcessId, string? BusinessKey, DateTimeOffset? StartedAfter, DateTimeOffset? StartedBefore);

public sealed record InstancePage(IReadOnlyList<InstanceView> Items, long? NextCursor);

public sealed record HistoryEntry(long Seq, long InstanceKey, long? ElementInstanceKey, string EventType, string? ElementId, JsonObject Payload, DateTimeOffset OccurredAt);

public sealed record HistoryPage(IReadOnlyList<HistoryEntry> Items, long? NextCursor);

public sealed record IncidentView(
    long Key,
    long InstanceKey,
    long? ElementInstanceKey,
    long? JobKey,
    string ErrorType,
    string ErrorMessage,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record IncidentPage(IReadOnlyList<IncidentView> Items, long? NextCursor);

/// <summary>Read side (API-004, API-010, API-012): queries never take instance locks.</summary>
public interface IInstanceQueries
{
    Task<InstanceView?> GetInstanceAsync(string tenantId, long instanceKey, CancellationToken cancellationToken);

    Task<InstancePage> ListInstancesAsync(string tenantId, InstanceFilter filter, int pageSize, long? afterKey, CancellationToken cancellationToken);

    /// <summary>Root-scope variables of the instance; null when the instance does not exist.</summary>
    Task<JsonObject?> GetVariablesAsync(string tenantId, long instanceKey, CancellationToken cancellationToken);

    Task<HistoryPage> GetHistoryAsync(string tenantId, long instanceKey, int pageSize, long? afterSeq, CancellationToken cancellationToken);

    Task<IncidentPage> ListIncidentsAsync(string tenantId, long? instanceKey, string? state, int pageSize, long? afterKey, CancellationToken cancellationToken);
}
