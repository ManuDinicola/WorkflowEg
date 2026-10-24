using System.Text.Json.Nodes;
using Dapper;
using Flowd.Application.Security;
using Npgsql;
using NpgsqlTypes;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Stored responses for <c>Idempotency-Key</c> replays (API-R1).</summary>
public sealed class PostgresIdempotencyStore(NpgsqlDataSource dataSource) : IIdempotencyStore
{
    public async Task<IdempotentResponse?> FindAsync(string scopedKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<(byte[] Hash, string Response, DateTime CreatedAt)?>(new CommandDefinition(
            "SELECT request_hash, response::text, created_at FROM idempotency_key WHERE key = @scopedKey",
            new { scopedKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is not { } r)
        {
            return null;
        }

        var stored = JsonNode.Parse(r.Response)!.AsObject();
        return new IdempotentResponse(
            r.Hash,
            stored["status"]!.GetValue<int>(),
            stored["contentType"]?.GetValue<string>(),
            Convert.FromBase64String(stored["body"]!.GetValue<string>()),
            PostgresInstanceTransaction.Utc(r.CreatedAt));
    }

    public async Task SaveAsync(string scopedKey, IdempotentResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var json = new JsonObject
        {
            ["status"] = response.Status,
            ["contentType"] = response.ContentType,
            ["body"] = Convert.ToBase64String(response.Body),
        }.ToJsonString();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "INSERT INTO idempotency_key (key, request_hash, response, created_at) VALUES (@k, @h, @r, @c) ON CONFLICT (key) DO NOTHING",
            connection);
        command.Parameters.AddWithValue("k", scopedKey);
        command.Parameters.AddWithValue("h", response.RequestHash);
        command.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Jsonb) { Value = json });
        command.Parameters.AddWithValue("c", response.CreatedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
