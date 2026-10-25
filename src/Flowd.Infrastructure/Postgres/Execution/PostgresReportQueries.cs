using Dapper;
using Flowd.Application.Queries;
using Npgsql;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Reports straight from the tables (FR-UI-013). Every query is bounded by a time range and an index-friendly filter.</summary>
public sealed class PostgresReportQueries(NpgsqlDataSource dataSource) : IReportQueries
{
    private const string Join = "FROM process_instance p JOIN process_definition d ON d.key = p.definition_key WHERE p.tenant_id = @tenantId AND (@process::text IS NULL OR d.bpmn_process_id = @process)";

    public async Task<IReadOnlyList<InstanceBucket>> InstancesOverTimeAsync(string tenantId, string? bpmnProcessId, DateTimeOffset since, DateTimeOffset until, bool hourly, CancellationToken cancellationToken)
    {
        var unit = hourly ? "hour" : "day";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new { tenantId, process = bpmnProcessId, from = since.UtcDateTime, to = until.UtcDateTime, unit };
        var started = (await connection.QueryAsync<(DateTime Bucket, int Count)>(new CommandDefinition(
            $"SELECT date_trunc(@unit, p.started_at AT TIME ZONE 'UTC'), count(*)::int {Join} AND p.started_at >= @from AND p.started_at < @to GROUP BY 1",
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToDictionary(r => r.Bucket, r => r.Count);
        var ended = (await connection.QueryAsync<(DateTime Bucket, string State, int Count)>(new CommandDefinition(
            $"SELECT date_trunc(@unit, p.ended_at AT TIME ZONE 'UTC'), p.state, count(*)::int {Join} AND p.ended_at >= @from AND p.ended_at < @to GROUP BY 1, 2",
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var buckets = new List<InstanceBucket>();
        var step = hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        for (var at = Truncate(since.UtcDateTime, hourly); at < until.UtcDateTime && buckets.Count < 24 * 366; at += step)
        {
            buckets.Add(new InstanceBucket(
                new DateTimeOffset(at, TimeSpan.Zero),
                started.GetValueOrDefault(at),
                ended.Where(e => e.Bucket == at && e.State == "COMPLETED").Sum(e => e.Count),
                ended.Where(e => e.Bucket == at && e.State == "TERMINATED").Sum(e => e.Count)));
        }

        return buckets;
    }

    public async Task<DurationReport> DurationsAsync(string tenantId, string? bpmnProcessId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleAsync<(int Count, double? Average, double? Median, double? P90, double? P99, double? Max)>(new CommandDefinition(
            $"""
            SELECT count(*)::int, avg(x)::float8, (percentile_cont(0.5) WITHIN GROUP (ORDER BY x))::float8, (percentile_cont(0.9) WITHIN GROUP (ORDER BY x))::float8,
                   (percentile_cont(0.99) WITHIN GROUP (ORDER BY x))::float8, max(x)::float8
            FROM (SELECT extract(epoch FROM p.ended_at - p.started_at) AS x {Join} AND p.state = 'COMPLETED' AND p.ended_at >= @from AND p.ended_at < @to) t
            """,
            new { tenantId, process = bpmnProcessId, from = since.UtcDateTime, to = until.UtcDateTime },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new DurationReport(row.Count, row.Average, row.Median, row.P90, row.P99, row.Max);
    }

    public async Task<IReadOnlyList<IncidentBucket>> IncidentsAsync(string tenantId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Type, int Raised, int Open)>(new CommandDefinition(
            """
            SELECT i.error_type, count(*)::int, (count(*) FILTER (WHERE i.state = 'OPEN'))::int
            FROM incident i JOIN process_instance p ON p.key = i.instance_key
            WHERE p.tenant_id = @tenantId AND i.created_at >= @from AND i.created_at < @to
            GROUP BY i.error_type ORDER BY 2 DESC LIMIT 50
            """,
            new { tenantId, from = since.UtcDateTime, to = until.UtcDateTime },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new IncidentBucket(r.Type, r.Raised, r.Open))];
    }

    public async Task<IReadOnlyList<DecisionUsage>> DecisionsAsync(string tenantId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Id, int Count, int Failures, double Average)>(new CommandDefinition(
            """
            SELECT decision_id, count(*)::int, (count(*) FILTER (WHERE error IS NOT NULL))::int, avg(duration_ms)::float8
            FROM decision_evaluation WHERE tenant_id = @tenantId AND evaluated_at >= @from AND evaluated_at < @to
            GROUP BY decision_id ORDER BY 2 DESC LIMIT 100
            """,
            new { tenantId, from = since.UtcDateTime, to = until.UtcDateTime },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new DecisionUsage(r.Id, r.Count, r.Failures, r.Average))];
    }

    private static DateTime Truncate(DateTime value, bool hourly) =>
        hourly ? new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Unspecified) : value.Date;
}
