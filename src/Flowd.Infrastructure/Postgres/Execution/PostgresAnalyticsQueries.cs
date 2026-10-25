using Dapper;
using Flowd.Application.Queries;
using Npgsql;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Counts, durations and the element heat map straight from the tables (FR-UI-004).</summary>
public sealed class PostgresAnalyticsQueries(NpgsqlDataSource dataSource) : IAnalyticsQueries
{
    public async Task<AnalyticsSummary> SummaryAsync(string tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var instances = await Counts(connection, "SELECT state, count(*)::int FROM process_instance WHERE tenant_id = @tenantId GROUP BY state", tenantId, cancellationToken).ConfigureAwait(false);
        var jobs = await Counts(connection, "SELECT state, count(*)::int FROM job WHERE tenant_id = @tenantId GROUP BY state", tenantId, cancellationToken).ConfigureAwait(false);
        var incidents = await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM incident i JOIN process_instance p ON p.key = i.instance_key WHERE p.tenant_id = @tenantId AND i.state = 'OPEN'",
            new { tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var tasks = await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM user_task WHERE tenant_id = @tenantId AND state = 'CREATED'",
            new { tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new AnalyticsSummary(instances, incidents, jobs, tasks);
    }

    public async Task<ProcessAnalytics> ProcessAsync(string tenantId, string bpmnProcessId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var states = await Counts(
            connection,
            "SELECT p.state, count(*)::int FROM process_instance p JOIN process_definition d ON d.key = p.definition_key WHERE p.tenant_id = @tenantId AND d.bpmn_process_id = @bpmnProcessId GROUP BY p.state",
            tenantId,
            cancellationToken,
            bpmnProcessId).ConfigureAwait(false);
        var duration = await connection.QuerySingleAsync<(double? Average, double? Max)>(new CommandDefinition(
            "SELECT avg(extract(epoch FROM p.ended_at - p.started_at))::float8, max(extract(epoch FROM p.ended_at - p.started_at))::float8 " +
            "FROM process_instance p JOIN process_definition d ON d.key = p.definition_key " +
            "WHERE p.tenant_id = @tenantId AND d.bpmn_process_id = @bpmnProcessId AND p.state = 'COMPLETED' AND p.ended_at IS NOT NULL",
            new { tenantId, bpmnProcessId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var elements = await connection.QueryAsync<(string ElementId, string ElementType, int Activations, double? Average)>(new CommandDefinition(
            "SELECT e.element_id, e.element_type, count(*)::int, avg(extract(epoch FROM e.ended_at - e.started_at))::float8 " +
            "FROM element_instance e JOIN process_instance p ON p.key = e.instance_key JOIN process_definition d ON d.key = p.definition_key " +
            "WHERE p.tenant_id = @tenantId AND d.bpmn_process_id = @bpmnProcessId GROUP BY e.element_id, e.element_type ORDER BY e.element_id",
            new { tenantId, bpmnProcessId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new ProcessAnalytics(bpmnProcessId, states, duration.Average, duration.Max, [.. elements.Select(e => new ElementStatistics(e.ElementId, e.ElementType, e.Activations, e.Average))]);
    }

    private static async Task<Dictionary<string, int>> Counts(NpgsqlConnection connection, string sql, string tenantId, CancellationToken cancellationToken, string? bpmnProcessId = null)
    {
        var rows = await connection.QueryAsync<(string Key, int Count)>(new CommandDefinition(sql, new { tenantId, bpmnProcessId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Key, r => r.Count, StringComparer.Ordinal);
    }
}
