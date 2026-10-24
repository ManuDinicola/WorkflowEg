using Dapper;
using Flowd.Application.Queries;
using Npgsql;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Job search for operators (FR-API-011).</summary>
public sealed class PostgresJobQueries(NpgsqlDataSource dataSource) : IJobQueries
{
    public async Task<JobPage> SearchAsync(string tenantId, JobFilter filter, int pageSize, long? afterKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT key AS Key, type AS Type, instance_key AS InstanceKey, element_id AS ElementId, state AS State, retries AS Retries, worker AS Worker, " +
            "deadline AS Deadline, error_message AS ErrorMessage, created_at AS CreatedAt FROM job " +
            "WHERE tenant_id = @tenantId AND key > @after AND (@type::text IS NULL OR type = @type) AND (@state::text IS NULL OR state = @state) " +
            "AND (@instance::bigint IS NULL OR instance_key = @instance) AND (@worker::text IS NULL OR worker = @worker) ORDER BY key LIMIT @take",
            new { tenantId, after = afterKey ?? 0L, type = filter.Type, state = filter.State?.ToUpperInvariant(), instance = filter.InstanceKey, worker = filter.Worker, take = pageSize + 1 },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows[..pageSize] : rows;
        return new JobPage([.. items.Select(r => r.ToView())], hasMore ? items[^1].Key : null);
    }

    private sealed class Row
    {
        public long Key { get; set; }

        public string Type { get; set; } = string.Empty;

        public long InstanceKey { get; set; }

        public string ElementId { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public int Retries { get; set; }

        public string? Worker { get; set; }

        public DateTime? Deadline { get; set; }

        public string? ErrorMessage { get; set; }

        public DateTime CreatedAt { get; set; }

        public JobView ToView() => new(Key, Type, InstanceKey, ElementId, State, Retries, Worker, Deadline is { } d ? PostgresInstanceTransaction.Utc(d) : null, ErrorMessage, PostgresInstanceTransaction.Utc(CreatedAt));
    }
}
