using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flowd.Contracts.Execution;

namespace Flowd.Worker.Client;

/// <summary>A job handed to a worker handler.</summary>
public sealed record WorkerJob(
    long Key,
    string Type,
    string ProcessInstanceKey,
    string ElementId,
    int Retries,
    DateTimeOffset Deadline,
    JsonObject Variables,
    IReadOnlyDictionary<string, string> CustomHeaders)
{
}

/// <summary>Factory helpers for handler results.</summary>
public static class JobResults
{
    public static JobResult Complete(JsonObject? variables = null) => new JobCompleted(variables);

    public static JobResult Fail(string message, int? retries = null, TimeSpan? backoff = null) => new JobFailed(message, retries, backoff);

    public static JobResult BpmnError(string code, string? message = null, JsonObject? variables = null) => new JobBpmnError(code, message, variables);
}

/// <summary>The handler finished the job; the variables are merged into the process (FR-JOB-002).</summary>
public sealed record JobCompleted(JsonObject? Variables);

/// <summary>The handler failed. Without <see cref="Retries"/> the job's remaining retries minus one are used (FR-JOB-003).</summary>
public sealed record JobFailed(string Message, int? Retries, TimeSpan? RetryBackoff);

/// <summary>The handler reports a BPMN error that a boundary event can catch (FR-JOB-005).</summary>
public sealed record JobBpmnError(string Code, string? Message, JsonObject? Variables);

/// <summary>What a worker handler returns (section 9.2).</summary>
public union JobResult(JobCompleted, JobFailed, JobBpmnError);

/// <summary>Thin typed client for the worker part of the Flowd API (section 9.2).</summary>
public sealed class FlowdClient(HttpClient http)
{
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Creates a client for <paramref name="baseAddress"/>; the API key, if any, is sent with every request.</summary>
    public static FlowdClient Create(Uri baseAddress, string? apiKey = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
#pragma warning disable CA2000 // the FlowdClient owns the HttpClient for its lifetime
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
#pragma warning restore CA2000
        http.BaseAddress = baseAddress;
        if (!string.IsNullOrEmpty(apiKey))
        {
            http.DefaultRequestHeaders.Add(ApiKeyHeader, apiKey);
        }

        return new FlowdClient(http);
    }

    /// <summary>Starts a long-polling worker for one job type.</summary>
    public JobWorker CreateWorker(string type, Func<WorkerJob, CancellationToken, Task<JobResult>> handler, JobWorkerOptions? options = null, TimeProvider? time = null) =>
        new(this, type, handler, options ?? new JobWorkerOptions(), time ?? TimeProvider.System);

    public async Task<IReadOnlyList<WorkerJob>> ActivateAsync(string type, JobWorkerOptions options, int maxJobs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = new ActivateJobsRequestDto(type, options.WorkerName, (long)options.JobTimeout.TotalMilliseconds, maxJobs, (long)options.PollTimeout.TotalMilliseconds, options.FetchVariables);
        using var response = await http.PostAsJsonAsync(new Uri("/v1/jobs/activate", UriKind.Relative), request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ActivateJobsResponseDto>(cancellationToken).ConfigureAwait(false) ?? new ActivateJobsResponseDto([]);
        return [.. body.Jobs.Select(j => new WorkerJob(long.Parse(j.Key, System.Globalization.CultureInfo.InvariantCulture), j.Type, j.ProcessInstanceKey, j.ElementId, j.Retries, j.Deadline, j.Variables, j.CustomHeaders))];
    }

    public Task ReportAsync(WorkerJob job, JobResult result, string worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return result switch
        {
            JobCompleted completed => PostAsync($"/v1/jobs/{job.Key}/complete", new CompleteJobRequestDto(completed.Variables, worker), cancellationToken),
            JobFailed failed => PostAsync($"/v1/jobs/{job.Key}/fail", new FailJobRequestDto(failed.Retries ?? Math.Max(job.Retries - 1, 0), failed.Message, failed.RetryBackoff is { } backoff ? (long)backoff.TotalMilliseconds : null, worker), cancellationToken),
            JobBpmnError error => PostAsync($"/v1/jobs/{job.Key}/error", new ThrowJobErrorRequestDto(error.Code, error.Message, error.Variables, worker), cancellationToken),
        };
    }

    private async Task PostAsync<T>(string path, T body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return; // already completed or taken over by another worker: at-least-once delivery, nothing to do (FR-JOB-002)
        }

        response.EnsureSuccessStatusCode();
    }
}
