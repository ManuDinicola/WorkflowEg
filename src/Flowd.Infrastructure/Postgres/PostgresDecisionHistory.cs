using System.Text.Json.Nodes;
using Dapper;
using Flowd.Application.Decisions;
using Npgsql;
using NpgsqlTypes;

namespace Flowd.Infrastructure.Postgres;

/// <summary>History of decision evaluations in the <c>decision_evaluation</c> table (FR-DMN-007).</summary>
public sealed class PostgresDecisionHistory(NpgsqlDataSource dataSource) : IDecisionHistory
{
    private const string Columns = "key AS Key, tenant_id AS TenantId, decision_id AS DecisionId, decision_key AS DecisionKey, evaluated_at AS EvaluatedAt, source AS Source, instance_key AS InstanceKey, duration_ms AS DurationMs, input::text AS Input, output::text AS Output, error AS Error";

    public async Task AppendAsync(DecisionEvaluationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO decision_evaluation (key, tenant_id, decision_id, decision_key, evaluated_at, source, instance_key, duration_ms, input, output, error)
            VALUES (@key, @tenant, @decision, @decisionKey, @at, @source, @instance, @duration, @input, @output, @error)
            """,
            connection);
        command.Parameters.AddWithValue("key", record.Key);
        command.Parameters.AddWithValue("tenant", record.TenantId);
        command.Parameters.AddWithValue("decision", record.DecisionId);
        command.Parameters.AddWithValue("decisionKey", (object?)record.DecisionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("at", record.EvaluatedAt.UtcDateTime);
        command.Parameters.AddWithValue("source", record.Source);
        command.Parameters.AddWithValue("instance", (object?)record.InstanceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("duration", record.DurationMs);
        command.Parameters.Add(new NpgsqlParameter("input", NpgsqlDbType.Jsonb) { Value = record.Input.ToJsonString() });
        command.Parameters.Add(new NpgsqlParameter("output", NpgsqlDbType.Jsonb) { Value = record.Output is null ? DBNull.Value : record.Output.ToJsonString() });
        command.Parameters.AddWithValue("error", (object?)record.Error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DecisionEvaluationPage> ListAsync(string tenantId, string? decisionId, long? instanceKey, int pageSize, long? afterKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM decision_evaluation
            WHERE tenant_id = @tenantId AND (@decisionId::text IS NULL OR decision_id = @decisionId) AND (@instanceKey::bigint IS NULL OR instance_key = @instanceKey)
              AND (@afterKey::bigint IS NULL OR key < @afterKey)
            ORDER BY key DESC LIMIT @take
            """,
            new { tenantId, decisionId, instanceKey, afterKey, take = pageSize + 1 },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var page = rows.Take(pageSize).Select(ToRecord).ToList();
        return new DecisionEvaluationPage(page, rows.Count > pageSize ? page[^1].Key : null);
    }

    public async Task<DecisionEvaluationRecord?> GetAsync(string tenantId, long key, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM decision_evaluation WHERE tenant_id = @tenantId AND key = @key",
            new { tenantId, key },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    private static DecisionEvaluationRecord ToRecord(Row r) => new(
        r.Key, r.TenantId, r.DecisionId, r.DecisionKey, new DateTimeOffset(DateTime.SpecifyKind(r.EvaluatedAt, DateTimeKind.Utc)), r.Source, r.InstanceKey, r.DurationMs,
        JsonNode.Parse(r.Input) ?? new JsonObject(), r.Output is null ? null : JsonNode.Parse(r.Output), r.Error);

    private sealed record Row(long Key, string TenantId, string DecisionId, long? DecisionKey, DateTime EvaluatedAt, string Source, long? InstanceKey, int DurationMs, string Input, string? Output, string? Error);
}
