namespace Flowd.Application.Queries;

/// <summary>Instances started, completed and terminated in one time bucket (an hour or a day).</summary>
public sealed record InstanceBucket(DateTimeOffset Start, int Started, int Completed, int Terminated);

/// <summary>How long completed instances took, in seconds.</summary>
public sealed record DurationReport(int Count, double? Average, double? Median, double? P90, double? P99, double? Max);

/// <summary>Incidents raised in the period by type, and how many of them are still open.</summary>
public sealed record IncidentBucket(string ErrorType, int Raised, int StillOpen);

/// <summary>Evaluations of one decision in the period: how many, how many failed, the average time in milliseconds.</summary>
public sealed record DecisionUsage(string DecisionId, int Evaluations, int Failures, double AverageMs);

/// <summary>Port: reports over the history of instances, incidents and decisions (FR-UI-013). All read-only, all limited to a tenant and a time range.</summary>
public interface IReportQueries
{
    /// <summary>A bucket for every hour or day of the range (empty buckets included), oldest first.</summary>
    Task<IReadOnlyList<InstanceBucket>> InstancesOverTimeAsync(string tenantId, string? bpmnProcessId, DateTimeOffset since, DateTimeOffset until, bool hourly, CancellationToken cancellationToken);

    Task<DurationReport> DurationsAsync(string tenantId, string? bpmnProcessId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);

    Task<IReadOnlyList<IncidentBucket>> IncidentsAsync(string tenantId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);

    Task<IReadOnlyList<DecisionUsage>> DecisionsAsync(string tenantId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);
}
