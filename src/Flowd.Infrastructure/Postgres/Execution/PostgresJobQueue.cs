using System.Text.Json.Nodes;
using Dapper;
using Flowd.Application.Execution;
using Flowd.Application.Jobs;
using Npgsql;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Finds the owning instance of a job or incident without locking anything (section 16).</summary>
public sealed class PostgresInstanceLookup(NpgsqlDataSource dataSource) : IInstanceLookup
{
    public async Task<long?> InstanceOfJobAsync(string tenantId, long jobKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT instance_key FROM job WHERE key = @jobKey AND tenant_id = @tenantId",
            new { jobKey, tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<long?> InstanceOfIncidentAsync(string tenantId, long incidentKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT i.instance_key FROM incident i JOIN process_instance p ON p.key = i.instance_key WHERE i.key = @incidentKey AND p.tenant_id = @tenantId",
            new { incidentKey, tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary>Job activation with <c>FOR UPDATE SKIP LOCKED</c>; never locks process instances (FR-JOB-001, FR-CON-004).</summary>
public sealed class PostgresJobQueue(NpgsqlDataSource dataSource) : IJobQueue
{
    private const int SweepBatchSize = 500;

    public async Task<IReadOnlyList<ActivatedJob>> ActivateAsync(ActivateJobsRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ActivatedRow>(new CommandDefinition(
            """
            WITH picked AS (
              SELECT key FROM job
              WHERE tenant_id = @TenantId AND type = @Type AND state = 'ACTIVATABLE'
                AND (retry_backoff_until IS NULL OR retry_backoff_until <= @now)
              ORDER BY created_at, key
              LIMIT @MaxJobs
              FOR UPDATE SKIP LOCKED)
            UPDATE job j SET state = 'ACTIVATED', worker = @Worker, deadline = @deadline
            FROM picked
            WHERE j.key = picked.key
            RETURNING j.key AS Key, j.type AS Type, j.instance_key AS InstanceKey, j.element_id AS ElementId, j.retries AS Retries,
                      j.deadline AS Deadline, j.variables::text AS Variables, j.custom_headers::text AS CustomHeaders, j.created_at AS CreatedAt
            """,
            new { request.TenantId, request.Type, request.MaxJobs, request.Worker, now = now.UtcDateTime, deadline = (now + request.Timeout).UtcDateTime },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.OrderBy(r => r.Key).Select(r => r.ToJob(request.FetchVariables))];
    }

    public async Task<IReadOnlyList<string>> TenantsWithActivatableAsync(string type, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return [.. await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT tenant_id FROM job WHERE type = @type AND state = 'ACTIVATABLE' AND (retry_backoff_until IS NULL OR retry_backoff_until <= @now)",
            new { type, now = now.UtcDateTime },
            cancellationToken: cancellationToken)).ConfigureAwait(false)];
    }

    public async Task<int> RequeueTimedOutAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE job SET state = 'ACTIVATABLE', worker = NULL, deadline = NULL
            WHERE key IN (
              SELECT key FROM job WHERE state = 'ACTIVATED' AND deadline < @now
              ORDER BY deadline LIMIT @batch FOR UPDATE SKIP LOCKED)
            """,
            new { now = now.UtcDateTime, batch = SweepBatchSize },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> UpdateTimeoutAsync(string tenantId, long jobKey, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET deadline = @deadline WHERE key = @jobKey AND tenant_id = @tenantId AND state = 'ACTIVATED'",
            new { deadline = deadline.UtcDateTime, jobKey, tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows > 0;
    }

    private sealed class ActivatedRow
    {
        public long Key { get; set; }

        public string Type { get; set; } = string.Empty;

        public long InstanceKey { get; set; }

        public string ElementId { get; set; } = string.Empty;

        public int Retries { get; set; }

        public DateTime Deadline { get; set; }

        public string Variables { get; set; } = "{}";

        public string CustomHeaders { get; set; } = "{}";

        public DateTime CreatedAt { get; set; }

        public ActivatedJob ToJob(IReadOnlyList<string>? fetch)
        {
            var all = JsonNode.Parse(Variables)!.AsObject();
            var variables = new JsonObject();
            foreach (var (name, value) in all.Where(p => fetch is null || fetch.Contains(p.Key, StringComparer.Ordinal)))
            {
                variables[name] = value?.DeepClone();
            }

            var headers = JsonNode.Parse(CustomHeaders)!.AsObject().ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal);
            return new ActivatedJob(Key, Type, InstanceKey, ElementId, Retries, PostgresInstanceTransaction.Utc(Deadline), variables, headers, PostgresInstanceTransaction.Utc(CreatedAt));
        }
    }
}
