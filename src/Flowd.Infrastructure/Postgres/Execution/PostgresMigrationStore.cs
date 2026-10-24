using System.Text.Json;
using Dapper;
using Flowd.Application.Migration;
using Npgsql;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Reads and rewrites the rows that tie a running instance to its process definition (FR-OPS-004).</summary>
public sealed class PostgresMigrationStore(NpgsqlDataSource dataSource) : IInstanceMigrationStore
{
    private static readonly string[] ElementTables = ["element_instance", "job", "user_task", "timer", "message_subscription"];

    public async Task<MigrationSnapshot?> GetSnapshotAsync(string tenantId, long instanceKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var head = await connection.QuerySingleOrDefaultAsync<(long DefinitionKey, string BpmnProcessId, string State)?>(new CommandDefinition(
            "SELECT p.definition_key, d.bpmn_process_id, p.state FROM process_instance p JOIN process_definition d ON d.key = p.definition_key WHERE p.key = @instanceKey AND p.tenant_id = @tenantId",
            new { instanceKey, tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        var live = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT element_id FROM element_instance WHERE instance_key = @instanceKey AND state NOT IN ('COMPLETED', 'TERMINATED')",
            new { instanceKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var referenced = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT element_id FROM job WHERE instance_key = @instanceKey AND state IN ('ACTIVATABLE', 'ACTIVATED')
            UNION SELECT element_id FROM user_task WHERE instance_key = @instanceKey AND state = 'CREATED'
            UNION SELECT element_id FROM timer WHERE instance_key = @instanceKey AND state = 'ACTIVE'
            UNION SELECT element_id FROM message_subscription WHERE instance_key = @instanceKey
            """,
            new { instanceKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        return new MigrationSnapshot(head.Value.DefinitionKey, head.Value.BpmnProcessId, head.Value.State, referenced, live);
    }

    public async Task<MigrationStoreOutcome> MigrateAsync(string tenantId, long instanceKey, long expectedDefinitionKey, long targetDefinitionKey, IReadOnlyDictionary<string, string> mapping, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await connection.QuerySingleOrDefaultAsync<(long DefinitionKey, string State)?>(new CommandDefinition(
            "SELECT definition_key, state FROM process_instance WHERE key = @instanceKey AND tenant_id = @tenantId FOR UPDATE",
            new { instanceKey, tenantId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (current is null)
        {
            return MigrationStoreOutcome.NotFound;
        }

        if (current.Value.State != "ACTIVE")
        {
            return MigrationStoreOutcome.NotActive;
        }

        if (current.Value.DefinitionKey != expectedDefinitionKey)
        {
            return MigrationStoreOutcome.DefinitionChanged;
        }

        await connection.ExecuteAsync(new CommandDefinition("UPDATE process_instance SET definition_key = @targetDefinitionKey WHERE key = @instanceKey", new { targetDefinitionKey, instanceKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE timer SET definition_key = @targetDefinitionKey WHERE instance_key = @instanceKey", new { targetDefinitionKey, instanceKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await RenameAsync(connection, transaction, instanceKey, mapping, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO history_event (instance_key, event_type, payload, occurred_at) VALUES (@instanceKey, 'INSTANCE_MIGRATED', @payload::jsonb, @at)",
            new { instanceKey, payload = JsonSerializer.Serialize(new { from = expectedDefinitionKey.ToString(System.Globalization.CultureInfo.InvariantCulture), to = targetDefinitionKey.ToString(System.Globalization.CultureInfo.InvariantCulture) }), at = now.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MigrationStoreOutcome.Migrated;
    }

    /// <summary>One statement per table with all renames at once, so swapping two ids does not chain.</summary>
    private static async Task RenameAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceKey, IReadOnlyDictionary<string, string> mapping, CancellationToken cancellationToken)
    {
        var changed = mapping.Where(m => !string.Equals(m.Key, m.Value, StringComparison.Ordinal)).ToList();
        if (changed.Count == 0)
        {
            return;
        }

        var froms = changed.Select(m => m.Key).ToArray();
        var tos = changed.Select(m => m.Value).ToArray();
        foreach (var table in ElementTables)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"UPDATE {table} t SET element_id = m.to_id FROM unnest(@froms::text[], @tos::text[]) AS m(from_id, to_id) WHERE t.instance_key = @instanceKey AND t.element_id = m.from_id",
                new { froms, tos, instanceKey },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }
}
