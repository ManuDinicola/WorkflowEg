namespace Flowd.Application.Retention;

/// <summary>How long finished data is kept (FR-DB-002). History is configured separately from the instances.</summary>
public sealed record RetentionPolicy(TimeSpan Instances, TimeSpan History, TimeSpan Outbox, int BatchSize)
{
    public static RetentionPolicy Default { get; } = new(TimeSpan.FromDays(30), TimeSpan.FromDays(30), TimeSpan.FromDays(7), 500);
}

/// <summary>What one janitor run removed.</summary>
public sealed record RetentionResult(int Instances, int HistoryEvents, int OutboxRows, int IdempotencyKeys)
{
    public int Total => Instances + HistoryEvents + OutboxRows + IdempotencyKeys;
}

/// <summary>Port: deletes data that is past its retention in small batches.</summary>
public interface IRetentionStore
{
    Task<RetentionResult> PurgeAsync(DateTimeOffset now, RetentionPolicy policy, CancellationToken cancellationToken);
}
