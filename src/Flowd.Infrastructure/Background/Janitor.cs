using Flowd.Application.Retention;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flowd.Infrastructure.Background;

/// <summary>Retention settings, bound from <c>Flowd:Retention</c>.</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Flowd:Retention";

    public int InstanceDays { get; init; } = 30;

    public int HistoryDays { get; init; } = 30;

    public int OutboxDays { get; init; } = 7;

    public int BatchSize { get; init; } = 500;

    public int IntervalSeconds { get; init; } = 60;
}

/// <summary>Background janitor: purges finished data past its retention in batches (FR-DB-002).</summary>
public sealed partial class Janitor(IRetentionStore store, IOptions<RetentionOptions> options, TimeProvider time, ILogger<Janitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(options.Value.IntervalSeconds, 1)), time);
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

    /// <summary>Runs one purge pass. Returns what was removed.</summary>
    public async Task<RetentionResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var policy = new RetentionPolicy(TimeSpan.FromDays(settings.InstanceDays), TimeSpan.FromDays(settings.HistoryDays), TimeSpan.FromDays(settings.OutboxDays), settings.BatchSize);
        try
        {
            var result = await store.PurgeAsync(time.GetUtcNow(), policy, cancellationToken).ConfigureAwait(false);
            if (result.Total > 0)
            {
                LogPurged(result.Instances, result.HistoryEvents, result.OutboxRows, result.IdempotencyKeys);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex);
            return new RetentionResult(0, 0, 0, 0);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Janitor removed {Instances} instance(s), {History} history event(s), {Outbox} outbox row(s), {Keys} idempotency key(s).")]
    private partial void LogPurged(int instances, int history, int outbox, int keys);

    [LoggerMessage(Level = LogLevel.Error, Message = "Janitor run failed; will retry.")]
    private partial void LogFailed(Exception exception);
}
