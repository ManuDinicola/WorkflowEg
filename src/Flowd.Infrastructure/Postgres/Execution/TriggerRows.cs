using System.Xml;
using Dapper;
using Flowd.Engine.Effects;
using Flowd.Engine.State;
using Npgsql;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Reads and writes the timer and message-subscription rows of one instance (sections 10 and 11).</summary>
internal static class TriggerRows
{
    public static string KindText(TriggerKind kind) => kind switch
    {
        TriggerKind.Catch => "CATCH",
        TriggerKind.Boundary => "BOUNDARY",
        TriggerKind.EventGateway => "EVENT_GATEWAY",
        TriggerKind.Start => "START",
        _ => throw new InvalidOperationException($"Unknown trigger kind {kind}."), // non-union: enum
    };

    public static TriggerKind ParseKind(string text) => text switch
    {
        "CATCH" => TriggerKind.Catch,
        "BOUNDARY" => TriggerKind.Boundary,
        "EVENT_GATEWAY" => TriggerKind.EventGateway,
        "START" => TriggerKind.Start,
        _ => throw new InvalidOperationException($"Unknown trigger kind '{text}'."), // non-union: database text
    };

    public static async Task LoadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EngineState state, CancellationToken cancellationToken)
    {
        var timers = await connection.QueryAsync<TimerRow>(new CommandDefinition(
            "SELECT key AS Key, element_instance_key AS ElementInstanceKey, definition_key AS DefinitionKey, element_id AS ElementId, kind AS Kind, " +
            "due_at AS DueAt, repetitions AS Repetitions, interval_iso AS IntervalIso, tenant_id AS TenantId FROM timer WHERE instance_key = @key AND state = 'ACTIVE'",
            new { key = state.InstanceKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var t in timers)
        {
            state.Timers[t.Key] = new TimerRecord
            {
                Key = t.Key,
                InstanceKey = state.InstanceKey,
                ElementInstanceKey = t.ElementInstanceKey,
                DefinitionKey = t.DefinitionKey,
                ElementId = t.ElementId,
                Kind = ParseKind(t.Kind),
                DueAt = PostgresInstanceTransaction.Utc(t.DueAt),
                Repetitions = t.Repetitions,
                Interval = t.IntervalIso is null ? null : XmlConvert.ToTimeSpan(t.IntervalIso),
                TenantId = t.TenantId,
            };
        }

        var subscriptions = await connection.QueryAsync<SubscriptionRow>(new CommandDefinition(
            "SELECT key AS Key, element_instance_key AS ElementInstanceKey, definition_key AS DefinitionKey, element_id AS ElementId, kind AS Kind, " +
            "message_name AS MessageName, correlation_key AS CorrelationKey, tenant_id AS TenantId, signal AS Signal FROM message_subscription WHERE instance_key = @key",
            new { key = state.InstanceKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var s in subscriptions)
        {
            state.Subscriptions[s.Key] = ToRecord(s, state.InstanceKey);
        }
    }

    public static SubscriptionRecord ToRecord(SubscriptionRow s, long? instanceKey) => new()
    {
        Key = s.Key,
        InstanceKey = instanceKey ?? s.InstanceKey,
        ElementInstanceKey = s.ElementInstanceKey,
        DefinitionKey = s.DefinitionKey,
        ElementId = s.ElementId,
        Kind = ParseKind(s.Kind),
        MessageName = s.MessageName,
        CorrelationKey = s.CorrelationKey,
        IsSignal = s.Signal,
        TenantId = s.TenantId,
    };

    public static void Write(NpgsqlBatch batch, EngineEffect effect)
    {
        switch (effect)
        {
            case TimerChanged timer:
                Add(batch, UpsertTimerSql, TimerParameters(timer.Snapshot));
                break;
            case SubscriptionChanged subscription:
                Add(batch, InsertSubscriptionSql, SubscriptionParameters(subscription.Snapshot));
                break;
            case SubscriptionClosed closed:
                Add(batch, "DELETE FROM message_subscription WHERE key = @k", new NpgsqlParameter("k", closed.Key));
                break;
            case MessageCorrelated correlated:
                Add(batch, "INSERT INTO message_correlation (message_key, definition_key) VALUES (@m, @d)", new NpgsqlParameter("m", correlated.MessageKey), new NpgsqlParameter("d", correlated.DefinitionKey));
                break;
        }
    }

    private const string UpsertTimerSql =
        "INSERT INTO timer (key, instance_key, element_instance_key, definition_key, element_id, kind, due_at, repetitions, interval_iso, state, tenant_id) " +
        "VALUES (@k, @i, @e, @d, @eid, @kind, @due, @rep, @interval, @state, @tenant) " +
        "ON CONFLICT (key) DO UPDATE SET due_at = EXCLUDED.due_at, repetitions = EXCLUDED.repetitions, state = EXCLUDED.state, lease_until = NULL";

    private const string InsertSubscriptionSql =
        "INSERT INTO message_subscription (key, instance_key, element_instance_key, definition_key, element_id, kind, message_name, correlation_key, tenant_id, signal) " +
        "VALUES (@k, @i, @e, @d, @eid, @kind, @name, @corr, @tenant, @sig) ON CONFLICT (key) DO NOTHING";

    private static NpgsqlParameter[] TimerParameters(TimerRecord t) =>
    [
        new("k", t.Key), new("i", (object?)t.InstanceKey ?? DBNull.Value), new("e", (object?)t.ElementInstanceKey ?? DBNull.Value), new("d", (object?)t.DefinitionKey ?? DBNull.Value),
        new("eid", t.ElementId), new("kind", KindText(t.Kind)), new("due", t.DueAt.UtcDateTime), new("rep", (object?)t.Repetitions ?? DBNull.Value),
        new("interval", t.Interval is { } interval ? XmlConvert.ToString(interval) : DBNull.Value), new("state", t.IsActive ? "ACTIVE" : "DONE"), new("tenant", t.TenantId),
    ];

    private static NpgsqlParameter[] SubscriptionParameters(SubscriptionRecord s) =>
    [
        new("k", s.Key), new("i", (object?)s.InstanceKey ?? DBNull.Value), new("e", (object?)s.ElementInstanceKey ?? DBNull.Value), new("d", s.DefinitionKey),
        new("eid", s.ElementId), new("kind", KindText(s.Kind)), new("name", s.MessageName), new("corr", s.CorrelationKey), new("tenant", s.TenantId), new("sig", s.IsSignal),
    ];

    private static void Add(NpgsqlBatch batch, string sql, params NpgsqlParameter[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        batch.BatchCommands.Add(command);
    }

    public sealed class SubscriptionRow
    {
        public long Key { get; set; }

        public long? InstanceKey { get; set; }

        public long? ElementInstanceKey { get; set; }

        public long DefinitionKey { get; set; }

        public string ElementId { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string MessageName { get; set; } = string.Empty;

        public string CorrelationKey { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;

        public bool Signal { get; set; }
    }

    private sealed class TimerRow
    {
        public long Key { get; set; }

        public long? ElementInstanceKey { get; set; }

        public long? DefinitionKey { get; set; }

        public string ElementId { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public DateTime DueAt { get; set; }

        public int? Repetitions { get; set; }

        public string? IntervalIso { get; set; }

        public string TenantId { get; set; } = string.Empty;
    }
}
