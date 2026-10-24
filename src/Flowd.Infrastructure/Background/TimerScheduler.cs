using Flowd.Application.Messaging;
using Flowd.Application.Timers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flowd.Infrastructure.Background;

/// <summary>Fires due timers every 250 ms; overdue timers fire on startup in due order (FR-TMR-002, FR-TMR-003).</summary>
public sealed partial class TimerScheduler(TimerService timers, TimeProvider time, ILogger<TimerScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

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

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Keep draining while there is work, so a backlog after downtime clears quickly.
            int handled;
            do
            {
                handled = await timers.RunDueAsync(cancellationToken).ConfigureAwait(false);
            }
            while (handled > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTimerRunFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Timer run failed; will retry.")]
    private partial void LogTimerRunFailed(Exception exception);
}

/// <summary>Removes expired buffered messages (FR-MSG-001).</summary>
public sealed partial class MessageExpirySweeper(IMessageStore store, TimeProvider time, ILogger<MessageExpirySweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

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
            await store.DeleteExpiredAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSweepFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Message expiry sweep failed; will retry.")]
    private partial void LogSweepFailed(Exception exception);
}
