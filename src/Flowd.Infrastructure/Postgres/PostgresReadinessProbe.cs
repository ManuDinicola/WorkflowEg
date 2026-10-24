using Flowd.Application.Readiness;
using Npgsql;

namespace Flowd.Infrastructure.Postgres;

/// <summary>Ready = database reachable and no pending migration (API-013).</summary>
public sealed class PostgresReadinessProbe(string connectionString, IMigrationRunner migrations) : IReadinessProbe
{
    public async Task<ReadinessStatus> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            return new ReadinessStatus(false, "database unreachable");
        }

        return migrations.IsCurrent()
            ? new ReadinessStatus(true, "ok")
            : new ReadinessStatus(false, "migrations pending");
    }
}
