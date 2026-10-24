using System.Text.Json.Nodes;
using Dapper;
using Flowd.Application.Forms;
using Npgsql;
using NpgsqlTypes;

namespace Flowd.Infrastructure.Postgres;

/// <summary>Dapper/Npgsql implementation of <see cref="IFormStore"/> (FR-UT-005).</summary>
public sealed class PostgresFormStore(NpgsqlDataSource dataSource) : IFormStore
{
    private const string Columns = "key AS Key, tenant_id AS TenantId, form_id AS FormId, version AS Version, resource_name AS ResourceName, deployed_at AS DeployedAt";

    public async Task<IReadOnlyList<DeployedForm>> DeployAsync(IReadOnlyList<FormDraft> drafts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DeployedForm>(drafts.Count);
        foreach (var draft in drafts)
        {
            result.Add(await DeployOneAsync(connection, transaction, draft, cancellationToken).ConfigureAwait(false));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<FormDocument?> FindAsync(string tenantId, string formId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns}, schema::text AS Schema FROM form_definition WHERE tenant_id = @tenantId AND form_id = @formId ORDER BY version DESC LIMIT 1",
            new { tenantId, formId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : new FormDocument(row.ToInfo(), JsonNode.Parse(row.Schema)!.AsObject());
    }

    public async Task<IReadOnlyList<FormInfo>> ListAsync(string tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return [.. (await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM form_definition WHERE tenant_id = @tenantId ORDER BY form_id, version",
            new { tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).Select(r => r.ToInfo())];
    }

    private static async Task<DeployedForm> DeployOneAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, FormDraft draft, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lockKey, 0))",
            new { lockKey = $"form|{draft.TenantId}|{draft.FormId}" },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var latest = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns}, checksum AS Checksum FROM form_definition WHERE tenant_id = @TenantId AND form_id = @FormId ORDER BY version DESC LIMIT 1",
            new { draft.TenantId, draft.FormId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (latest is not null && latest.Checksum.AsSpan().SequenceEqual(draft.Checksum))
        {
            return new DeployedForm(latest.ToInfo(), IsNewVersion: false);
        }

        var version = (latest?.Version ?? 0) + 1;
        await using var insert = new NpgsqlCommand(
            "INSERT INTO form_definition (key, tenant_id, form_id, version, resource_name, schema, checksum, deployed_at) VALUES (@key, @tenant, @form, @version, @name, @schema, @checksum, @at)",
            connection,
            transaction);
        insert.Parameters.Add(new NpgsqlParameter("key", draft.Key));
        insert.Parameters.Add(new NpgsqlParameter("tenant", draft.TenantId));
        insert.Parameters.Add(new NpgsqlParameter("form", draft.FormId));
        insert.Parameters.Add(new NpgsqlParameter("version", version));
        insert.Parameters.Add(new NpgsqlParameter("name", draft.ResourceName));
        insert.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Jsonb) { Value = draft.Schema.ToJsonString() });
        insert.Parameters.Add(new NpgsqlParameter("checksum", draft.Checksum));
        insert.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = draft.DeployedAt.UtcDateTime });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new DeployedForm(new FormInfo(draft.Key, draft.TenantId, draft.FormId, version, draft.ResourceName, draft.DeployedAt), IsNewVersion: true);
    }

    private sealed class Row
    {
        public long Key { get; set; }

        public string TenantId { get; set; } = string.Empty;

        public string FormId { get; set; } = string.Empty;

        public int Version { get; set; }

        public string ResourceName { get; set; } = string.Empty;

        public DateTime DeployedAt { get; set; }

        public byte[] Checksum { get; set; } = [];

        public string Schema { get; set; } = "{}";

        public FormInfo ToInfo() => new(Key, TenantId, FormId, Version, ResourceName, new DateTimeOffset(DateTime.SpecifyKind(DeployedAt, DateTimeKind.Utc)));
    }
}
