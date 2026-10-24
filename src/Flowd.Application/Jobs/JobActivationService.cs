namespace Flowd.Application.Jobs;

/// <summary>Long-poll job activation (FR-JOB-001): polls the queue until jobs appear or the request times out.</summary>
public sealed class JobActivationService(IJobQueue queue, TimeProvider time, Flowd.Application.Observability.EngineTelemetry telemetry)
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public async Task<IReadOnlyList<ActivatedJob>> ActivateAsync(
        ActivateJobsRequest request,
        TimeSpan? requestTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wait = Min(requestTimeout ?? DefaultRequestTimeout, MaxRequestTimeout);
        var giveUpAt = time.GetUtcNow() + wait;
        while (true)
        {
            var now = time.GetUtcNow();
            var jobs = await queue.ActivateAsync(request, now, cancellationToken).ConfigureAwait(false);
            telemetry.JobsActivated(request.Type, jobs.Count, jobs.Select(job => now - job.CreatedAt));
            if (jobs.Count > 0 || time.GetUtcNow() >= giveUpAt)
            {
                return jobs;
            }

            try
            {
                await Task.Delay(PollInterval, time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return [];
            }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
