using Dapper;
using Flowd.Application.Retention;
using Npgsql;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Deletes finished instances and old history, outbox rows and idempotency keys in batches (FR-DB-002).</summary>
public sealed class PostgresRetentionStore(NpgsqlDataSource dataSource) : IRetentionStore
{
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);

    public async Task<RetentionResult> PurgeAsync(DateTimeOffset now, RetentionPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var instances = await PurgeInstancesAsync(connection, now - policy.Instances, policy.BatchSize, cancellationToken).ConfigureAwait(false);
        var history = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM history_event WHERE seq IN (SELECT seq FROM history_event WHERE occurred_at < @cutoff ORDER BY seq LIMIT @batch)",
            new { cutoff = (now - policy.History).UtcDateTime, batch = policy.BatchSize },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var outbox = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM outbox WHERE seq IN (SELECT seq FROM outbox WHERE published_at IS NOT NULL AND published_at < @cutoff ORDER BY seq LIMIT @batch)",
            new { cutoff = (now - policy.Outbox).UtcDateTime, batch = policy.BatchSize },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var keys = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM idempotency_key WHERE key IN (SELECT key FROM idempotency_key WHERE created_at < @cutoff LIMIT @batch)",
            new { cutoff = (now - IdempotencyRetention).UtcDateTime, batch = policy.BatchSize },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new RetentionResult(instances, history, outbox, keys);
    }

    private static async Task<int> PurgeInstancesAsync(NpgsqlConnection connection, DateTimeOffset cutoff, int batch, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var keys = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT key FROM process_instance WHERE state IN ('COMPLETED', 'TERMINATED') AND ended_at < @cutoff ORDER BY ended_at LIMIT @batch FOR UPDATE SKIP LOCKED",
            new { cutoff = cutoff.UtcDateTime, batch },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        if (keys.Length == 0)
        {
            return 0;
        }

        foreach (var table in new[] { "variable", "job", "incident", "user_task", "timer", "message_subscription", "element_instance" })
        {
            await connection.ExecuteAsync(new CommandDefinition($"DELETE FROM {table} WHERE instance_key = ANY(@keys)", new { keys }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM process_instance WHERE key = ANY(@keys)", new { keys }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return keys.Length;
    }
}
