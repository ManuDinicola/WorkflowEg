using Flowd.Application.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flowd.Infrastructure.Background;

/// <summary>
/// Moves jobs whose deadline passed back to ACTIVATABLE without decrementing retries (FR-JOB-004). Leased with
/// <c>SKIP LOCKED</c>, so several nodes can run it safely (FR-ARCH-004). Delivery is at-least-once: workers must be idempotent.
/// </summary>
public sealed partial class JobTimeoutSweeper(IJobQueue queue, TimeProvider time, Flowd.Application.Observability.EngineTelemetry telemetry, ILogger<JobTimeoutSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    internal async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var requeued = await queue.RequeueTimedOutAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            telemetry.JobsTimedOut(requeued);
            if (requeued > 0)
            {
                LogRequeued(requeued);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSweepFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Requeued {Count} timed-out job(s).")]
    private partial void LogRequeued(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job timeout sweep failed; will retry.")]
    private partial void LogSweepFailed(Exception exception);
}
