using Dapper;
using Flowd.Application.Decisions;
using Npgsql;

namespace Flowd.Infrastructure.Postgres;

/// <summary>Dapper/Npgsql implementation of <see cref="IDecisionStore"/> (FR-DMN-003).</summary>
public sealed class PostgresDecisionStore(NpgsqlDataSource dataSource) : IDecisionStore
{
    private const string Columns = "key AS Key, tenant_id AS TenantId, decision_id AS DecisionId, version AS Version, resource_name AS ResourceName, deployed_at AS DeployedAt";

    public async Task<IReadOnlyList<DeployedDecision>> DeployAsync(IReadOnlyList<DecisionDraft> drafts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DeployedDecision>(drafts.Count);
        foreach (var draft in drafts)
        {
            result.Add(await DeployOneAsync(connection, transaction, draft, cancellationToken).ConfigureAwait(false));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<DecisionInfo?> FindAsync(string tenantId, string decisionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QuerySingleOrDefaultAsync<Latest>(new CommandDefinition(
            $"SELECT {Columns} FROM decision_definition WHERE tenant_id = @tenantId AND decision_id = @decisionId ORDER BY version DESC LIMIT 1",
            new { tenantId, decisionId },
            cancellationToken: cancellationToken)).ConfigureAwait(false))?.ToInfo();
    }

    public async Task<byte[]?> GetResourceAsync(string tenantId, long key, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
            "SELECT resource FROM decision_definition WHERE tenant_id = @tenantId AND key = @key",
            new { tenantId, key },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DecisionInfo>> ListAsync(string tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return [.. (await connection.QueryAsync<Latest>(new CommandDefinition(
            $"SELECT {Columns} FROM decision_definition WHERE tenant_id = @tenantId ORDER BY decision_id, version",
            new { tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).Select(r => r.ToInfo())];
    }

    private static async Task<DeployedDecision> DeployOneAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DecisionDraft draft, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lockKey, 0))",
            new { lockKey = $"decision|{draft.TenantId}|{draft.DecisionId}" },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var latest = await connection.QuerySingleOrDefaultAsync<Latest>(new CommandDefinition(
            $"SELECT {Columns}, checksum AS Checksum FROM decision_definition WHERE tenant_id = @TenantId AND decision_id = @DecisionId ORDER BY version DESC LIMIT 1",
            new { draft.TenantId, draft.DecisionId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (latest is not null && latest.Checksum.AsSpan().SequenceEqual(draft.Checksum))
        {
            return new DeployedDecision(latest.ToInfo(), IsNewVersion: false);
        }

        var version = (latest?.Version ?? 0) + 1;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO decision_definition (key, tenant_id, decision_id, version, resource_name, resource, checksum, deployed_at)
            VALUES (@Key, @TenantId, @DecisionId, @version, @ResourceName, @Resource, @Checksum, @DeployedAt)
            """,
            new { draft.Key, draft.TenantId, draft.DecisionId, version, draft.ResourceName, draft.Resource, draft.Checksum, draft.DeployedAt },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new DeployedDecision(new DecisionInfo(draft.Key, draft.TenantId, draft.DecisionId, version, draft.ResourceName, draft.DeployedAt), IsNewVersion: true);
    }

    private sealed class Latest
    {
        public long Key { get; init; }

        public string TenantId { get; init; } = string.Empty;

        public string DecisionId { get; init; } = string.Empty;

        public int Version { get; init; }

        public string ResourceName { get; init; } = string.Empty;

        public DateTime DeployedAt { get; init; }

        public byte[] Checksum { get; init; } = [];

        public DecisionInfo ToInfo() => new(Key, TenantId, DecisionId, Version, ResourceName, new DateTimeOffset(DateTime.SpecifyKind(DeployedAt, DateTimeKind.Utc)));
    }
}
