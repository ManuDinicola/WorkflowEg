using Dapper;
using Flowd.Application.Deployment;
using Npgsql;

namespace Flowd.Infrastructure.Postgres;

/// <summary>Dapper/Npgsql implementation of <see cref="IProcessDefinitionStore"/> (section 15, FR-DEP-001..003).</summary>
public sealed class PostgresProcessDefinitionStore(NpgsqlDataSource dataSource) : IProcessDefinitionStore
{
    private const string Columns =
        "key AS Key, tenant_id AS TenantId, bpmn_process_id AS BpmnProcessId, version AS Version, resource_name AS ResourceName, deployed_at AS DeployedAt";

    public async Task<IReadOnlyList<DeployedDefinition>> DeployAsync(IReadOnlyList<DefinitionDraft> drafts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DeployedDefinition>(drafts.Count);
        foreach (var draft in drafts)
        {
            result.Add(await DeployOneAsync(connection, transaction, draft, cancellationToken).ConfigureAwait(false));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ProcessDefinitionInfo?> GetAsync(string tenantId, long key, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = new CommandDefinition(
            $"SELECT {Columns} FROM process_definition WHERE tenant_id = @tenantId AND key = @key",
            new { tenantId, key },
            cancellationToken: cancellationToken);
        return (await connection.QuerySingleOrDefaultAsync<Row>(command).ConfigureAwait(false))?.ToInfo();
    }

    public async Task<ProcessDefinitionInfo?> FindAsync(string tenantId, string bpmnProcessId, int? version, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = new CommandDefinition(
            $"SELECT {Columns} FROM process_definition WHERE tenant_id = @tenantId AND bpmn_process_id = @bpmnProcessId AND (@version::int IS NULL OR version = @version) ORDER BY version DESC LIMIT 1",
            new { tenantId, bpmnProcessId, version },
            cancellationToken: cancellationToken);
        return (await connection.QuerySingleOrDefaultAsync<Row>(command).ConfigureAwait(false))?.ToInfo();
    }

    public async Task<byte[]?> GetResourceAsync(string tenantId, long key, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = new CommandDefinition(
            "SELECT resource FROM process_definition WHERE tenant_id = @tenantId AND key = @key",
            new { tenantId, key },
            cancellationToken: cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<byte[]>(command).ConfigureAwait(false);
    }

    public async Task<DeleteDefinitionOutcome> DeleteAsync(string tenantId, long key, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var exists = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT key FROM process_definition WHERE key = @key AND tenant_id = @tenantId FOR UPDATE",
            new { key, tenantId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (exists is null)
        {
            return DeleteDefinitionOutcome.NotFound;
        }

        var used = await connection.QuerySingleAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM process_instance WHERE definition_key = @key)", new { key }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (used)
        {
            return DeleteDefinitionOutcome.InUse;
        }

        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM timer WHERE definition_key = @key AND instance_key IS NULL", new { key }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM message_subscription WHERE definition_key = @key AND instance_key IS NULL", new { key }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM process_definition WHERE key = @key", new { key }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return DeleteDefinitionOutcome.Deleted;
    }

    public async Task<DefinitionPage> ListAsync(string tenantId, int pageSize, long? afterKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = new CommandDefinition(
            $"SELECT {Columns} FROM process_definition WHERE tenant_id = @tenantId AND key > @after ORDER BY key LIMIT @take",
            new { tenantId, after = afterKey ?? 0L, take = pageSize + 1 },
            cancellationToken: cancellationToken);
        var rows = (await connection.QueryAsync<Row>(command).ConfigureAwait(false)).Select(r => r.ToInfo()).ToList();
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows[..pageSize] : rows;
        return new DefinitionPage(items, hasMore ? items[^1].Key : null);
    }

    private static async Task<DeployedDefinition> DeployOneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DefinitionDraft draft,
        CancellationToken cancellationToken)
    {
        // Serialize concurrent deployments of the same process id so versions stay gapless and unique.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lockKey, 0))",
            new { lockKey = $"{draft.TenantId}|{draft.BpmnProcessId}" },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var latest = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns}, checksum AS Checksum FROM process_definition WHERE tenant_id = @TenantId AND bpmn_process_id = @BpmnProcessId ORDER BY version DESC LIMIT 1",
            new { draft.TenantId, draft.BpmnProcessId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (latest is not null && latest.Checksum!.AsSpan().SequenceEqual(draft.Checksum))
        {
            return new DeployedDefinition(latest.ToInfo(), IsNewVersion: false);
        }

        var version = (latest?.Version ?? 0) + 1;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO process_definition (key, bpmn_process_id, version, resource_name, resource, checksum, tenant_id, deployed_at)
            VALUES (@Key, @BpmnProcessId, @version, @ResourceName, @Resource, @Checksum, @TenantId, @DeployedAt)
            """,
            new { draft.Key, draft.BpmnProcessId, version, draft.ResourceName, draft.Resource, draft.Checksum, draft.TenantId, DeployedAt = draft.DeployedAt.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var info = new ProcessDefinitionInfo(draft.Key, draft.TenantId, draft.BpmnProcessId, version, draft.ResourceName, draft.DeployedAt);
        return new DeployedDefinition(info, IsNewVersion: true);
    }

    private sealed class Row
    {
        public long Key { get; set; }

        public string TenantId { get; set; } = string.Empty;

        public string BpmnProcessId { get; set; } = string.Empty;

        public int Version { get; set; }

        public string ResourceName { get; set; } = string.Empty;

        public DateTime DeployedAt { get; set; }

        public byte[]? Checksum { get; set; }

        public ProcessDefinitionInfo ToInfo() =>
            new(Key, TenantId, BpmnProcessId, Version, ResourceName, new DateTimeOffset(DateTime.SpecifyKind(DeployedAt, DateTimeKind.Utc)));
    }

}
