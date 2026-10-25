using Npgsql;

namespace Flowd.Infrastructure.Keys;

/// <summary>
/// Claims the node id of this process on the database (FR-DOM-002). Keys contain the node id, so two running nodes with the same id would create
/// clashing keys. The claim is a PostgreSQL session advisory lock on a connection that stays open for the life of the process: a second node with
/// the same id on the same database is refused at startup with a clear message, and a node that dies releases its id by itself (its connection ends).
/// </summary>
public sealed class NodeLease : IAsyncDisposable
{
    private const long KeyBase = 0x666C_6F77_6400_0000; // "flowd" in the high bytes, the node id in the low ones

    private readonly NpgsqlConnection _connection;

    private NodeLease(NpgsqlConnection connection) => _connection = connection;

    /// <summary>Waits up to <paramref name="wait"/> for the id to be free (a node that was just killed may still hold it for a moment), then gives up with an exception.</summary>
    public static async Task<NodeLease> AcquireAsync(string connectionString, int nodeId, TimeSpan wait, CancellationToken cancellationToken)
    {
        // a separate, non-pooled connection: it is held for the whole process and must never be handed to anything else
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
#pragma warning disable CA2000 // ownership passes to the NodeLease, or the catch below disposes it
        var connection = new NpgsqlConnection(builder.ConnectionString);
#pragma warning restore CA2000
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var deadline = DateTimeOffset.UtcNow + wait;
            while (true)
            {
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
                command.Parameters.AddWithValue("key", KeyBase + nodeId);
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                {
                    return new NodeLease(connection);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new InvalidOperationException($"Node id {nodeId} is already used by another running Flowd node on this database. Give every node its own Flowd__NodeId (0 to 1023); two nodes with the same id would create clashing keys.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
