namespace Flowd.Worker.Client;

/// <summary>Tuning of a <see cref="JobWorker"/>.</summary>
public sealed class JobWorkerOptions
{
    public string WorkerName { get; init; } = Environment.MachineName;

    /// <summary>How long a job stays locked to this worker before it is handed to another one (FR-JOB-004).</summary>
    public TimeSpan JobTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Server-side long-poll duration for an activation request (FR-JOB-001).</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxConcurrentJobs { get; init; } = 4;

    public IReadOnlyList<string>? FetchVariables { get; init; }

    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Long-polling worker (section 9.2): fetches jobs, runs the handler with bounded concurrency, reports the result, retries
/// transport errors with jittered exponential backoff and stops gracefully when cancelled. Delivery is at-least-once: handlers
/// must be idempotent.
/// </summary>
public sealed class JobWorker(FlowdClient client, string type, Func<WorkerJob, CancellationToken, Task<JobResult>> handler, JobWorkerOptions options, TimeProvider time)
{
    private long _handled;

    /// <summary>Number of jobs whose result was reported.</summary>
    public long Handled => Interlocked.Read(ref _handled);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var slots = new SemaphoreSlim(options.MaxConcurrentJobs, options.MaxConcurrentJobs);
        var running = new List<Task>();
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
                var free = 1 + slots.CurrentCount;
                var jobs = await client.ActivateAsync(type, options, free, stoppingToken).ConfigureAwait(false);
                failures = 0;
                if (jobs.Count == 0)
                {
                    slots.Release();
                    continue;
                }

                slots.Release(); // give back the probe slot, then take one per job
                foreach (var job in jobs)
                {
                    await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
                    running.Add(RunJobAsync(job, () => slots.Release(), stoppingToken));
                }

                running.RemoveAll(t => t.IsCompleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException)
            {
                failures++;
                await DelayAsync(Backoff(failures), stoppingToken).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(running).ConfigureAwait(false); // graceful shutdown: let in-flight handlers report
        slots.Dispose();
    }

    private TimeSpan Backoff(int failures)
    {
        var exponential = options.MinBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 10));
        var capped = Math.Min(exponential, options.MaxBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped * (0.5 + (Random.Shared.NextDouble() / 2))); // jitter between 50 % and 100 %
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task RunJobAsync(WorkerJob job, Action release, CancellationToken stoppingToken)
    {
        try
        {
            JobResult result;
            try
            {
                result = await handler(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return; // shutting down mid-job: the deadline hands it to another worker
            }
#pragma warning disable CA1031 // a handler failure must become a failed job, never kill the worker
            catch (Exception ex)
#pragma warning restore CA1031
            {
                result = JobResults.Fail(ex.Message);
            }

            await ReportWithRetryAsync(job, result).ConfigureAwait(false);
            Interlocked.Increment(ref _handled);
        }
        finally
        {
            release();
        }
    }

    private async Task ReportWithRetryAsync(WorkerJob job, JobResult result)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await client.ReportAsync(job, result, options.WorkerName, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException) when (++attempt < 5)
            {
                await DelayAsync(Backoff(attempt), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
