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
    string? BusinessKey,
    IReadOnlyList<string>? Tags = null);

/// <summary>Instance search: <c>VariableName</c> with <c>VariableValueJson</c> matches a root-scope variable; <c>Descending</c> lists the newest first.</summary>
public sealed record InstanceFilter(string? State, string? BpmnProcessId, string? BusinessKey, DateTimeOffset? StartedAfter, DateTimeOffset? StartedBefore, string? VariableName = null, string? VariableValueJson = null, bool Descending = false, IReadOnlyList<string>? AllowedProcessIds = null, string? Tag = null);

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

    /// <summary>The live element instances of the instance; null when the instance does not exist.</summary>
    Task<IReadOnlyList<ElementView>?> ListLiveElementsAsync(string tenantId, long instanceKey, CancellationToken cancellationToken);

    Task<HistoryPage> GetHistoryAsync(string tenantId, long instanceKey, int pageSize, long? afterSeq, CancellationToken cancellationToken);

    Task<IncidentPage> ListIncidentsAsync(string tenantId, long? instanceKey, string? state, int pageSize, long? afterKey, CancellationToken cancellationToken);
}

/// <summary>A running element instance of an instance (a token or an active scope), for operators who modify the instance.</summary>
public sealed record ElementView(long Key, string ElementId, string ElementType, string State, long ScopeKey);

/// <summary>A job as seen by operators searching the queue (FR-API-011).</summary>
public sealed record JobView(long Key, string Type, long InstanceKey, string ElementId, string State, int Retries, string? Worker, DateTimeOffset? Deadline, string? ErrorMessage, DateTimeOffset CreatedAt);

public sealed record JobFilter(string? Type, string? State, long? InstanceKey, string? Worker);

public sealed record JobPage(IReadOnlyList<JobView> Items, long? NextCursor);

/// <summary>Port: job search for operators.</summary>
public interface IJobQueries
{
    Task<JobPage> SearchAsync(string tenantId, JobFilter filter, int pageSize, long? afterKey, CancellationToken cancellationToken);
}

/// <summary>Counts for the overview page and monitoring (FR-UI-004).</summary>
public sealed record AnalyticsSummary(IReadOnlyDictionary<string, int> InstancesByState, int OpenIncidents, IReadOnlyDictionary<string, int> JobsByState, int OpenUserTasks);

/// <summary>Per element of a process: how often it ran and how long it took (the heat map).</summary>
public sealed record ElementStatistics(string ElementId, string ElementType, int Activations, double? AverageSeconds);

/// <summary>Statistics of one process id over all its versions.</summary>
public sealed record ProcessAnalytics(string BpmnProcessId, IReadOnlyDictionary<string, int> InstancesByState, double? AverageDurationSeconds, double? MaxDurationSeconds, IReadOnlyList<ElementStatistics> Elements);

/// <summary>Port: read-only statistics.</summary>
public interface IAnalyticsQueries
{
    Task<AnalyticsSummary> SummaryAsync(string tenantId, CancellationToken cancellationToken);

    Task<ProcessAnalytics> ProcessAsync(string tenantId, string bpmnProcessId, CancellationToken cancellationToken);
}
