using Flowd.Application.Decisions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flowd.Infrastructure.Background;

/// <summary>Runs the internal worker of business rule tasks with a called decision (FR-DMN-004).</summary>
public sealed partial class DecisionWorker(DecisionJobProcessor processor, TimeProvider time, ILogger<DecisionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await processor.RunOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Decision worker run failed; will retry.")]
    private partial void LogFailed(Exception exception);
}
