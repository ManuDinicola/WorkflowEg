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
            stored["body"] is { } body ? Convert.FromBase64String(body.GetValue<string>()) : [],
            PostgresInstanceTransaction.Utc(r.CreatedAt));
    }

    public async Task<bool> TryReserveAsync(string scopedKey, byte[] requestHash, DateTimeOffset now, DateTimeOffset retentionCutoff, DateTimeOffset staleCutoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestHash);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO idempotency_key (key, request_hash, response, created_at) VALUES (@k, @h, '{"status":0}'::jsonb, @now)
            ON CONFLICT (key) DO UPDATE SET request_hash = EXCLUDED.request_hash, response = EXCLUDED.response, created_at = EXCLUDED.created_at
            WHERE ((idempotency_key.response->>'status')::int = 0 AND idempotency_key.created_at < @stale)
               OR ((idempotency_key.response->>'status')::int <> 0 AND idempotency_key.created_at < @retention)
            """,
            connection);
        command.Parameters.AddWithValue("k", scopedKey);
        command.Parameters.AddWithValue("h", requestHash);
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        command.Parameters.AddWithValue("stale", staleCutoff.UtcDateTime);
        command.Parameters.AddWithValue("retention", retentionCutoff.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
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
            """
            INSERT INTO idempotency_key (key, request_hash, response, created_at) VALUES (@k, @h, @r, @c)
            ON CONFLICT (key) DO UPDATE SET request_hash = EXCLUDED.request_hash, response = EXCLUDED.response, created_at = EXCLUDED.created_at
            WHERE (idempotency_key.response->>'status')::int = 0
            """,
            connection);
        command.Parameters.AddWithValue("k", scopedKey);
        command.Parameters.AddWithValue("h", response.RequestHash);
        command.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Jsonb) { Value = json });
        command.Parameters.AddWithValue("c", response.CreatedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(string scopedKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM idempotency_key WHERE key = @scopedKey AND (response->>'status')::int = 0",
            new { scopedKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
