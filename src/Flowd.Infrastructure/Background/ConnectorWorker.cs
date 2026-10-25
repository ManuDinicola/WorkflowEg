using Flowd.Application.Connectors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flowd.Infrastructure.Background;

/// <summary>Runs the internal worker of the built-in connectors (FR-CON-010).</summary>
public sealed partial class ConnectorWorker(ConnectorJobProcessor processor, TimeProvider time, ILogger<ConnectorWorker> logger) : BackgroundService
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Connector worker run failed; will retry.")]
    private partial void LogFailed(Exception exception);
}
