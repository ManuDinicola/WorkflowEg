using Dapper;
using Flowd.Application.Operations;
using Npgsql;

namespace Flowd.Infrastructure.Keys;

/// <summary>Reads the running nodes from the advisory locks that <see cref="NodeLease"/> holds, so a node that died disappears at once (FR-OPS-011).</summary>
public sealed class PostgresNodeDirectory(NpgsqlDataSource dataSource) : INodeDirectory
{
    public async Task<IReadOnlyList<int>> RunningNodeIdsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var keys = await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT DISTINCT ((classid::bigint << 32) | objid::bigint) FROM pg_locks WHERE locktype = 'advisory' AND granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. keys.Where(NodeLease.IsNodeKey).Select(NodeLease.NodeIdOf).Order()];
    }
}
