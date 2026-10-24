using System.Text.Json.Nodes;

namespace Flowd.Application.Jobs;

/// <summary>Worker request to activate jobs of one type (FR-JOB-001).</summary>
public sealed record ActivateJobsRequest(
    string TenantId,
    string Type,
    string Worker,
    TimeSpan Timeout,
    int MaxJobs,
    IReadOnlyList<string>? FetchVariables);

/// <summary>A job handed to a worker (section 14.4).</summary>
public sealed record ActivatedJob(
    long Key,
    string Type,
    long InstanceKey,
    string ElementId,
    int Retries,
    DateTimeOffset Deadline,
    JsonObject Variables,
    IReadOnlyDictionary<string, string> CustomHeaders);

/// <summary>Port: the job queue. Never locks process instances (FR-CON-004).</summary>
public interface IJobQueue
{
    /// <summary>Atomically moves up to <c>MaxJobs</c> activatable jobs to ACTIVATED, oldest first (FR-JOB-001, FR-JOB-007).</summary>
    Task<IReadOnlyList<ActivatedJob>> ActivateAsync(ActivateJobsRequest request, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns jobs whose deadline passed to ACTIVATABLE without touching retries (FR-JOB-004). Returns how many.</summary>
    Task<int> RequeueTimedOutAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Moves the deadline of an ACTIVATED job (FR-JOB-006). False when the job is not activated.</summary>
    Task<bool> UpdateTimeoutAsync(string tenantId, long jobKey, DateTimeOffset deadline, CancellationToken cancellationToken);
}
