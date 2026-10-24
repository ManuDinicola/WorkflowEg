using System.Text.Json.Nodes;
using Dapper;
using Flowd.Application.Queries;
using Npgsql;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Read-only queries over instances, history and incidents (API-004, API-010, API-012).</summary>
public sealed class PostgresInstanceQueries(NpgsqlDataSource dataSource) : IInstanceQueries
{
    private const string InstanceSelect =
        "SELECT p.key AS Key, p.definition_key AS DefinitionKey, d.bpmn_process_id AS BpmnProcessId, d.version AS Version, p.state AS State, " +
        "p.started_at AS StartedAt, p.ended_at AS EndedAt, p.business_key AS BusinessKey " +
        "FROM process_instance p JOIN process_definition d ON d.key = p.definition_key";

    public async Task<InstanceView?> GetInstanceAsync(string tenantId, long instanceKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<InstanceRow>(new CommandDefinition(
            $"{InstanceSelect} WHERE p.key = @instanceKey AND p.tenant_id = @tenantId",
            new { instanceKey, tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToView();
    }

    public async Task<InstancePage> ListInstancesAsync(string tenantId, InstanceFilter filter, int pageSize, long? afterKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<InstanceRow>(new CommandDefinition(
            $"{InstanceSelect} WHERE p.tenant_id = @tenantId AND ((@desc AND p.key < @afterDesc) OR (NOT @desc AND p.key > @after)) " +
            "AND (@varName::text IS NULL OR EXISTS (SELECT 1 FROM variable v WHERE v.scope_key = p.key AND v.name = @varName AND v.value = @varValue::jsonb)) " +
            "AND (@state::text IS NULL OR p.state = @state) AND (@process::text IS NULL OR d.bpmn_process_id = @process) " +
            "AND (@business::text IS NULL OR p.business_key = @business) " +
            "AND (@from::timestamptz IS NULL OR p.started_at >= @from) AND (@to::timestamptz IS NULL OR p.started_at < @to) " +
            "ORDER BY CASE WHEN @desc THEN -p.key ELSE p.key END LIMIT @take",
            new
            {
                tenantId,
                after = afterKey ?? 0L,
                afterDesc = afterKey ?? long.MaxValue,
                desc = filter.Descending,
                varName = filter.VariableName,
                varValue = filter.VariableValueJson,
                state = filter.State?.ToUpperInvariant(),
                process = filter.BpmnProcessId,
                business = filter.BusinessKey,
                from = filter.StartedAfter?.UtcDateTime,
                to = filter.StartedBefore?.UtcDateTime,
                take = pageSize + 1,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows[..pageSize] : rows;
        return new InstancePage([.. items.Select(r => r.ToView())], hasMore ? items[^1].Key : null);
    }

    public async Task<JsonObject?> GetVariablesAsync(string tenantId, long instanceKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var exists = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT key FROM process_instance WHERE key = @instanceKey AND tenant_id = @tenantId",
            new { instanceKey, tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (exists is null)
        {
            return null;
        }

        var rows = await connection.QueryAsync<(string Name, string Value)>(new CommandDefinition(
            "SELECT name, value::text FROM variable WHERE scope_key = @instanceKey ORDER BY name",
            new { instanceKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var result = new JsonObject();
        foreach (var (name, value) in rows)
        {
            result[name] = JsonNode.Parse(value);
        }

        return result;
    }

    public async Task<IReadOnlyList<ElementView>?> ListLiveElementsAsync(string tenantId, long instanceKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var exists = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT key FROM process_instance WHERE key = @instanceKey AND tenant_id = @tenantId",
            new { instanceKey, tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (exists is null)
        {
            return null;
        }

        var rows = await connection.QueryAsync<(long Key, string ElementId, string ElementType, string State, long ScopeKey)>(new CommandDefinition(
            "SELECT key, element_id, element_type, state, scope_key FROM element_instance WHERE instance_key = @instanceKey AND state NOT IN ('COMPLETED', 'TERMINATED') ORDER BY key",
            new { instanceKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new ElementView(r.Key, r.ElementId, r.ElementType, r.State, r.ScopeKey))];
    }

    public async Task<HistoryPage> GetHistoryAsync(string tenantId, long instanceKey, int pageSize, long? afterSeq, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<HistoryRow>(new CommandDefinition(
            "SELECT h.seq AS Seq, h.instance_key AS InstanceKey, h.element_instance_key AS ElementInstanceKey, h.event_type AS EventType, h.element_id AS ElementId, " +
            "h.payload::text AS Payload, h.occurred_at AS OccurredAt " +
            "FROM history_event h JOIN process_instance p ON p.key = h.instance_key " +
            "WHERE h.instance_key = @instanceKey AND p.tenant_id = @tenantId AND h.seq > @after ORDER BY h.seq LIMIT @take",
            new { instanceKey, tenantId, after = afterSeq ?? 0L, take = pageSize + 1 },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows[..pageSize] : rows;
        return new HistoryPage([.. items.Select(r => r.ToEntry())], hasMore ? items[^1].Seq : null);
    }

    public async Task<IncidentPage> ListIncidentsAsync(string tenantId, long? instanceKey, string? state, int pageSize, long? afterKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<IncidentRow>(new CommandDefinition(
            "SELECT i.key AS Key, i.instance_key AS InstanceKey, i.element_instance_key AS ElementInstanceKey, i.job_key AS JobKey, i.error_type AS ErrorType, " +
            "i.error_message AS ErrorMessage, i.state AS State, i.created_at AS CreatedAt, i.resolved_at AS ResolvedAt " +
            "FROM incident i JOIN process_instance p ON p.key = i.instance_key " +
            "WHERE p.tenant_id = @tenantId AND i.key > @after AND (@instanceKey::bigint IS NULL OR i.instance_key = @instanceKey) " +
            "AND (@state::text IS NULL OR i.state = @state) ORDER BY i.key LIMIT @take",
            new { tenantId, after = afterKey ?? 0L, instanceKey, state = state?.ToUpperInvariant(), take = pageSize + 1 },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows[..pageSize] : rows;
        return new IncidentPage([.. items.Select(r => r.ToView())], hasMore ? items[^1].Key : null);
    }

    private sealed class InstanceRow
    {
        public long Key { get; set; }

        public long DefinitionKey { get; set; }

        public string BpmnProcessId { get; set; } = string.Empty;

        public int Version { get; set; }

        public string State { get; set; } = string.Empty;

        public DateTime StartedAt { get; set; }

        public DateTime? EndedAt { get; set; }

        public string? BusinessKey { get; set; }

        public InstanceView ToView() => new(
            Key, DefinitionKey, BpmnProcessId, Version, State,
            PostgresInstanceTransaction.Utc(StartedAt), EndedAt is { } e ? PostgresInstanceTransaction.Utc(e) : null, BusinessKey);
    }

    private sealed class HistoryRow
    {
        public long Seq { get; set; }

        public long InstanceKey { get; set; }

        public long? ElementInstanceKey { get; set; }

        public string EventType { get; set; } = string.Empty;

        public string? ElementId { get; set; }

        public string Payload { get; set; } = "{}";

        public DateTime OccurredAt { get; set; }

        public HistoryEntry ToEntry() => new(
            Seq, InstanceKey, ElementInstanceKey, EventType, ElementId, JsonNode.Parse(Payload)!.AsObject(), PostgresInstanceTransaction.Utc(OccurredAt));
    }

    private sealed class IncidentRow
    {
        public long Key { get; set; }

        public long InstanceKey { get; set; }

        public long? ElementInstanceKey { get; set; }

        public long? JobKey { get; set; }

        public string ErrorType { get; set; } = string.Empty;

        public string ErrorMessage { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public DateTime? ResolvedAt { get; set; }

        public IncidentView ToView() => new(
            Key, InstanceKey, ElementInstanceKey, JobKey, ErrorType, ErrorMessage, State,
            PostgresInstanceTransaction.Utc(CreatedAt), ResolvedAt is { } r ? PostgresInstanceTransaction.Utc(r) : null);
    }
}
