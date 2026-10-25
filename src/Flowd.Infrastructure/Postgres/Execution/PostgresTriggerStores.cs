using System.Text.Json.Nodes;
using System.Xml;
using Dapper;
using Flowd.Application.Messaging;
using Flowd.Application.Timers;
using Flowd.Engine.State;
using Npgsql;
using NpgsqlTypes;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Timer leasing with <c>FOR UPDATE SKIP LOCKED</c> so several nodes never fire the same timer twice (FR-TMR-002, FR-ARCH-004).</summary>
public sealed class PostgresTimerQueue(NpgsqlDataSource dataSource) : ITimerQueue
{
    public async Task<IReadOnlyList<TimerLease>> LeaseDueAsync(DateTimeOffset now, int limit, TimeSpan leaseFor, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<LeaseRow>(new CommandDefinition(
            """
            WITH due AS (
              SELECT key FROM timer
              WHERE state = 'ACTIVE' AND due_at <= @now AND (lease_until IS NULL OR lease_until <= @now)
              ORDER BY due_at, key LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE timer t SET lease_until = @leaseUntil FROM due WHERE t.key = due.key
            RETURNING t.key AS Key, t.instance_key AS InstanceKey, t.definition_key AS DefinitionKey, t.element_id AS ElementId, t.tenant_id AS TenantId,
                      t.kind AS Kind, t.due_at AS DueAt, t.repetitions AS Repetitions, t.interval_iso AS IntervalIso
            """,
            new { now = now.UtcDateTime, limit, leaseUntil = (now + leaseFor).UtcDateTime },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.OrderBy(r => r.DueAt).ThenBy(r => r.Key).Select(r => r.ToLease())];
    }

    public async Task AdvanceStartTimerAsync(long timerKey, DateTimeOffset? nextDue, int? repetitions, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE timer SET due_at = COALESCE(@due, due_at), repetitions = @repetitions, lease_until = NULL, state = CASE WHEN @due::timestamptz IS NULL THEN 'DONE' ELSE state END WHERE key = @timerKey",
            new { due = nextDue?.UtcDateTime, repetitions, timerKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed class LeaseRow
    {
        public long Key { get; set; }

        public long? InstanceKey { get; set; }

        public long? DefinitionKey { get; set; }

        public string ElementId { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public DateTime DueAt { get; set; }

        public int? Repetitions { get; set; }

        public string? IntervalIso { get; set; }

        public TimerLease ToLease() => new(
            Key, InstanceKey, DefinitionKey, ElementId, TenantId, TriggerRows.ParseKind(Kind), PostgresInstanceTransaction.Utc(DueAt), Repetitions,
            IntervalIso is null ? null : XmlConvert.ToTimeSpan(IntervalIso));
    }
}

/// <summary>Start timers and message start subscriptions of deployed definitions (FR-TMR-005, FR-MSG-004).</summary>
public sealed class PostgresStartTriggerStore(NpgsqlDataSource dataSource) : IStartTriggerStore
{
    public async Task ReplaceAsync(
        string tenantId,
        string bpmnProcessId,
        long definitionKey,
        IReadOnlyList<StartTimerDraft> timers,
        IReadOnlyList<StartMessageDraft> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(messages);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string OldDefinitions = "SELECT key FROM process_definition WHERE tenant_id = @tenantId AND bpmn_process_id = @bpmnProcessId";
        var scope = new { tenantId, bpmnProcessId };
        await connection.ExecuteAsync(new CommandDefinition($"DELETE FROM timer WHERE kind = 'START' AND tenant_id = @tenantId AND definition_key IN ({OldDefinitions})", scope, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition($"DELETE FROM message_subscription WHERE kind = 'START' AND tenant_id = @tenantId AND definition_key IN ({OldDefinitions})", scope, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var timer in timers)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO timer (key, definition_key, element_id, kind, due_at, repetitions, interval_iso, state, tenant_id) VALUES (@Key, @definitionKey, @ElementId, 'START', @due, @Repetitions, @interval, 'ACTIVE', @tenantId)",
                new { timer.Key, definitionKey, timer.ElementId, due = timer.DueAt.UtcDateTime, timer.Repetitions, interval = timer.Interval is { } i ? XmlConvert.ToString(i) : null, tenantId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        foreach (var message in messages)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO message_subscription (key, definition_key, element_id, kind, message_name, correlation_key, tenant_id, signal) VALUES (@Key, @definitionKey, @ElementId, 'START', @MessageName, '', @tenantId, @IsSignal)",
                new { message.Key, definitionKey, message.ElementId, message.MessageName, message.IsSignal, tenantId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Message buffer and subscription lookup (section 11).</summary>
public sealed class PostgresMessageStore(NpgsqlDataSource dataSource) : IMessageStore
{
    private const string SubscriptionColumns =
        "key AS Key, instance_key AS InstanceKey, element_instance_key AS ElementInstanceKey, definition_key AS DefinitionKey, element_id AS ElementId, " +
        "kind AS Kind, message_name AS MessageName, correlation_key AS CorrelationKey, tenant_id AS TenantId, signal AS Signal";

    public async Task<bool> TryBufferAsync(BufferedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "INSERT INTO buffered_message (key, name, correlation_key, message_id, variables, ttl_until, tenant_id, created_at) VALUES (@k, @n, @c, @m, @v, @t, @tenant, @created) ON CONFLICT DO NOTHING",
            connection);
        command.Parameters.AddWithValue("k", message.Key);
        command.Parameters.AddWithValue("n", message.Name);
        command.Parameters.AddWithValue("c", message.CorrelationKey);
        command.Parameters.AddWithValue("m", (object?)message.MessageId ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = message.Variables.ToJsonString() });
        command.Parameters.AddWithValue("t", message.ExpiresAt.UtcDateTime);
        command.Parameters.AddWithValue("tenant", message.TenantId);
        command.Parameters.AddWithValue("created", message.CreatedAt.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<SubscriptionRecord>> FindSubscriptionsAsync(string tenantId, string name, string correlationKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<TriggerRows.SubscriptionRow>(new CommandDefinition(
            $"SELECT {SubscriptionColumns} FROM message_subscription WHERE tenant_id = @tenantId AND message_name = @name AND correlation_key = @correlationKey AND kind <> 'START' AND NOT signal ORDER BY key",
            new { tenantId, name, correlationKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => TriggerRows.ToRecord(r, null))];
    }

    public async Task<IReadOnlyList<SubscriptionRecord>> FindStartSubscriptionsAsync(string tenantId, string name, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<TriggerRows.SubscriptionRow>(new CommandDefinition(
            $"SELECT {SubscriptionColumns} FROM message_subscription WHERE tenant_id = @tenantId AND message_name = @name AND kind = 'START' AND NOT signal ORDER BY key",
            new { tenantId, name },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => TriggerRows.ToRecord(r, null))];
    }

    public async Task<IReadOnlyList<BufferedMessage>> FindBufferedAsync(string tenantId, string name, string correlationKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<BufferedRow>(new CommandDefinition(
            "SELECT key AS Key, name AS Name, correlation_key AS CorrelationKey, message_id AS MessageId, variables::text AS Variables, ttl_until AS ExpiresAt, tenant_id AS TenantId, created_at AS CreatedAt " +
            "FROM buffered_message WHERE tenant_id = @tenantId AND name = @name AND correlation_key = @correlationKey AND ttl_until > @now ORDER BY created_at, key",
            new { tenantId, name, correlationKey, now = now.UtcDateTime },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new BufferedMessage(
            r.Key, r.TenantId, r.Name, r.CorrelationKey, r.MessageId, JsonNode.Parse(r.Variables)!.AsObject(),
            PostgresInstanceTransaction.Utc(r.ExpiresAt), PostgresInstanceTransaction.Utc(r.CreatedAt)))];
    }

    public async Task<bool> IsCorrelatedAsync(long messageKey, long definitionKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM message_correlation WHERE message_key = @messageKey AND definition_key = @definitionKey)",
            new { messageKey, definitionKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var scope = new { now = now.UtcDateTime };
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM message_correlation WHERE message_key IN (SELECT key FROM buffered_message WHERE ttl_until <= @now)", scope, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var deleted = await connection.ExecuteAsync(new CommandDefinition("DELETE FROM buffered_message WHERE ttl_until <= @now", scope, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private sealed class BufferedRow
    {
        public long Key { get; set; }

        public string Name { get; set; } = string.Empty;

        public string CorrelationKey { get; set; } = string.Empty;

        public string? MessageId { get; set; }

        public string Variables { get; set; } = "{}";

        public DateTime ExpiresAt { get; set; }

        public string TenantId { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
    }
}

/// <summary>Finds the running child instances of a call activity so they can be cancelled with it (FR-EXEC-015).</summary>
public sealed class PostgresChildInstanceStore(NpgsqlDataSource dataSource) : Flowd.Application.Execution.IChildInstanceStore
{
    public async Task<IReadOnlyList<long>> FindActiveChildrenAsync(string tenantId, long parentElementInstanceKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var keys = await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT key FROM process_instance WHERE tenant_id = @tenantId AND parent_element_instance_key = @parentElementInstanceKey AND state = 'ACTIVE' ORDER BY key",
            new { tenantId, parentElementInstanceKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. keys];
    }
}

/// <summary>API keys stored as SHA-256 hashes (FR-SEC-001).</summary>
public sealed class PostgresApiKeyStore(NpgsqlDataSource dataSource) : Flowd.Application.Security.IApiKeyStore
{
    public async Task<Flowd.Application.Security.ApiKeyInfo?> FindAsync(byte[] keyHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<(string Name, string Roles, string TenantId, string Processes)?>(new CommandDefinition(
            "SELECT name, roles, tenant_id, processes FROM api_key WHERE key_hash = @keyHash AND revoked_at IS NULL",
            new { keyHash },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is { } r
            ? new Flowd.Application.Security.ApiKeyInfo(r.Name, r.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), r.TenantId, r.Processes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : null;
    }

    public async Task CreateAsync(byte[] keyHash, Flowd.Application.Security.ApiKeyInfo info, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO api_key (key_hash, name, roles, tenant_id, processes, created_at) VALUES (@keyHash, @name, @roles, @tenant, @processes, @created)",
            new { keyHash, name = info.Name, roles = string.Join(',', info.Roles), tenant = info.TenantId, processes = string.Join(',', info.Processes ?? []), created = createdAt.UtcDateTime },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
