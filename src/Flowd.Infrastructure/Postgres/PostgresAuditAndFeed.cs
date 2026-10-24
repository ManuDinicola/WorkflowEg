using System.Text.Json.Nodes;
using Dapper;
using Flowd.Application.Audit;
using Flowd.Application.Queries;
using Npgsql;

namespace Flowd.Infrastructure.Postgres;

/// <summary>Append-only audit log in PostgreSQL (FR-SEC-005).</summary>
public sealed class PostgresAuditLog(NpgsqlDataSource dataSource) : IAuditLog
{
    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO audit_log (occurred_at, tenant_id, actor, method, path, status) VALUES (@at, @TenantId, @Actor, @Method, @Path, @Status)",
            new { at = entry.OccurredAt.UtcDateTime, entry.TenantId, entry.Actor, entry.Method, entry.Path, entry.Status },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AuditPage> ListAsync(string tenantId, int pageSize, long? afterSeq, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT seq AS Seq, occurred_at AS OccurredAt, tenant_id AS TenantId, actor AS Actor, method AS Method, path AS Path, status AS Status " +
            "FROM audit_log WHERE tenant_id = @tenantId AND seq > @after ORDER BY seq LIMIT @take",
            new { tenantId, after = afterSeq ?? 0L, take = pageSize + 1 },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows[..pageSize] : rows;
        return new AuditPage([.. items.Select(r => r.ToEntry())], hasMore ? items[^1].Seq : null);
    }

    private sealed class Row
    {
        public long Seq { get; set; }

        public DateTime OccurredAt { get; set; }

        public string TenantId { get; set; } = string.Empty;

        public string Actor { get; set; } = string.Empty;

        public string Method { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public int Status { get; set; }

        public AuditEntry ToEntry() => new(Seq, new DateTimeOffset(DateTime.SpecifyKind(OccurredAt, DateTimeKind.Utc)), TenantId, Actor, Method, Path, Status);
    }
}

/// <summary>Reads the lifecycle events of all instances of a tenant in global order (FR-API-010).</summary>
public sealed class PostgresEventFeed(NpgsqlDataSource dataSource) : IEventFeed
{
    public async Task<IReadOnlyList<FeedEvent>> ReadAsync(string tenantId, long afterSeq, int take, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT h.seq AS Seq, h.instance_key AS InstanceKey, h.event_type AS EventType, h.element_id AS ElementId, h.payload::text AS Payload, h.occurred_at AS OccurredAt " +
            "FROM history_event h JOIN process_instance p ON p.key = h.instance_key " +
            "WHERE p.tenant_id = @tenantId AND h.seq > @afterSeq ORDER BY h.seq LIMIT @take",
            new { tenantId, afterSeq, take },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToEvent())];
    }

    private sealed class Row
    {
        public long Seq { get; set; }

        public long InstanceKey { get; set; }

        public string EventType { get; set; } = string.Empty;

        public string? ElementId { get; set; }

        public string Payload { get; set; } = "{}";

        public DateTime OccurredAt { get; set; }

        public FeedEvent ToEvent() => new(Seq, InstanceKey, EventType, ElementId, JsonNode.Parse(Payload)!.AsObject(), new DateTimeOffset(DateTime.SpecifyKind(OccurredAt, DateTimeKind.Utc)));
    }
}
