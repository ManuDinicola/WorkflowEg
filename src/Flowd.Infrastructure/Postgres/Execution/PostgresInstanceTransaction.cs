using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using Flowd.Application.Execution;
using Flowd.Engine.Effects;
using Flowd.Engine.State;
using Npgsql;
using NpgsqlTypes;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Opens one Postgres transaction per command (FR-ARCH-002).</summary>
public sealed class PostgresInstanceTransactionFactory(NpgsqlDataSource dataSource, IProcessModelProvider models, Microsoft.Extensions.Options.IOptions<FlowdOptions> options) : IInstanceTransactionFactory
{
    public async Task<IInstanceTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition("SET LOCAL lock_timeout = '5s'", transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return new PostgresInstanceTransaction(connection, transaction, models, options.Value.OutboxActive);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Locks and loads an instance, then applies interpreter effects in order (FR-ARCH-002, FR-CON-001, FR-CON-005).</summary>
public sealed class PostgresInstanceTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, IProcessModelProvider models, bool outboxEnabled) : IInstanceTransaction
{
    private const string LockTimeoutSqlState = "55P03";

    public async Task<EngineState?> LockAsync(string tenantId, long instanceKey, CancellationToken cancellationToken)
    {
        InstanceRow? row;
        try
        {
            row = await connection.QuerySingleOrDefaultAsync<InstanceRow>(new CommandDefinition(
                "SELECT key AS Key, definition_key AS DefinitionKey, business_key AS BusinessKey, started_at AS StartedAt, ended_at AS EndedAt, state AS State, " +
                "parent_instance_key AS ParentInstanceKey, parent_element_instance_key AS ParentElementInstanceKey " +
                "FROM process_instance WHERE key = @instanceKey AND tenant_id = @tenantId FOR UPDATE",
                new { instanceKey, tenantId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == LockTimeoutSqlState)
        {
            throw new InstanceBusyException($"Process instance {instanceKey} is locked by another command.");
        }

        if (row is null)
        {
            return null;
        }

        var graph = await models.GetGraphAsync(tenantId, row.DefinitionKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Definition {row.DefinitionKey} of instance {instanceKey} is missing.");
        var state = new EngineState(row.Key, row.DefinitionKey, graph, tenantId, Utc(row.StartedAt))
        {
            BusinessKey = row.BusinessKey,
            ParentInstanceKey = row.ParentInstanceKey,
            ParentElementInstanceKey = row.ParentElementInstanceKey,
            EndedAt = row.EndedAt is { } ended ? Utc(ended) : null,
            State = ParseInstanceState(row.State),
        };
        await InstanceLoader.LoadAsync(connection, transaction, state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task ApplyAsync(EngineState state, IReadOnlyList<EngineEffect> effects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(effects);
        await using var batch = new NpgsqlBatch(connection, transaction);
        EffectWriter.AddAll(batch, state.InstanceKey, state.TenantId, effects, outboxEnabled);
        if (batch.BatchCommands.Count == 0)
        {
            return;
        }

        try
        {
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && string.Equals(ex.TableName, "message_correlation", StringComparison.Ordinal))
        {
            throw new CorrelationConflictException("The message already reached this definition.");
        }
    }

    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static InstanceState ParseInstanceState(string value) => value switch
    {
        "ACTIVE" => InstanceState.Active,
        "COMPLETED" => InstanceState.Completed,
        "TERMINATED" => InstanceState.Terminated,
        _ => throw new InvalidOperationException($"Unknown instance state '{value}'."), // non-union: database text
    };

    private sealed class InstanceRow
    {
        public long Key { get; set; }

        public long DefinitionKey { get; set; }

        public string? BusinessKey { get; set; }

        public DateTime StartedAt { get; set; }

        public DateTime? EndedAt { get; set; }

        public string State { get; set; } = string.Empty;

        public long? ParentInstanceKey { get; set; }

        public long? ParentElementInstanceKey { get; set; }
    }
}

/// <summary>Reads the live parts of an instance into an <see cref="EngineState"/>.</summary>
internal static class InstanceLoader
{
    public static async Task LoadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EngineState state, CancellationToken cancellationToken)
    {
        var key = state.InstanceKey;
        var elements = await connection.QueryAsync<ElementRow>(new CommandDefinition(
            "SELECT key AS Key, element_id AS ElementId, element_type AS ElementType, scope_key AS ScopeKey, state AS State, incoming_flow_id AS IncomingFlowId, loop_counter AS LoopCounter, started_at AS StartedAt, ended_at AS EndedAt " +
            "FROM element_instance WHERE instance_key = @key AND state NOT IN ('COMPLETED', 'TERMINATED')",
            new { key }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var e in elements)
        {
            state.Elements[e.Key] = new ElementInstance
            {
                Key = e.Key,
                ElementId = e.ElementId,
                ElementType = e.ElementType,
                ScopeKey = e.ScopeKey,
                State = Enum.Parse<ElementState>(e.State, ignoreCase: true),
                IncomingFlowId = e.IncomingFlowId,
                LoopCounter = e.LoopCounter,
                StartedAt = PostgresInstanceTransaction.Utc(e.StartedAt),
                EndedAt = e.EndedAt is { } end ? PostgresInstanceTransaction.Utc(end) : null,
            };
        }

        await LoadVariablesAsync(connection, transaction, state, cancellationToken).ConfigureAwait(false);
        await LoadJobsAsync(connection, transaction, state, cancellationToken).ConfigureAwait(false);
        await LoadIncidentsAsync(connection, transaction, state, cancellationToken).ConfigureAwait(false);
        await TriggerRows.LoadAsync(connection, transaction, state, cancellationToken).ConfigureAwait(false);
        await UserTaskRows.LoadAsync(connection, transaction, state, cancellationToken).ConfigureAwait(false);
    }

    private static async Task LoadVariablesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EngineState state, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<(long Scope, string Name, string Value)>(new CommandDefinition(
            "SELECT scope_key, name, value::text FROM variable WHERE instance_key = @key",
            new { key = state.InstanceKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var (scope, name, value) in rows)
        {
            state.Variables.SetIn(scope, name, JsonNode.Parse(value));
        }
    }

    private static async Task LoadJobsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EngineState state, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<JobRow>(new CommandDefinition(
            "SELECT key AS Key, element_instance_key AS ElementInstanceKey, element_id AS ElementId, type AS Type, state AS State, retries AS Retries, " +
            "retry_backoff_until AS RetryBackoffUntil, deadline AS Deadline, worker AS Worker, custom_headers::text AS CustomHeaders, variables::text AS Variables, " +
            "result_variables::text AS ResultVariables, error_message AS ErrorMessage, tenant_id AS TenantId, created_at AS CreatedAt " +
            "FROM job WHERE instance_key = @key FOR UPDATE",
            new { key = state.InstanceKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var j in rows)
        {
            state.Jobs[j.Key] = new JobRecord
            {
                Key = j.Key,
                InstanceKey = state.InstanceKey,
                ElementInstanceKey = j.ElementInstanceKey,
                ElementId = j.ElementId,
                Type = j.Type,
                State = Enum.Parse<JobState>(j.State.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
                Retries = j.Retries,
                RetryBackoffUntil = j.RetryBackoffUntil is { } b ? PostgresInstanceTransaction.Utc(b) : null,
                Deadline = j.Deadline is { } d ? PostgresInstanceTransaction.Utc(d) : null,
                Worker = j.Worker,
                CustomHeaders = JsonNode.Parse(j.CustomHeaders)!.AsObject().ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal),
                Variables = JsonNode.Parse(j.Variables)!.AsObject(),
                ResultVariables = j.ResultVariables is null ? null : JsonNode.Parse(j.ResultVariables)!.AsObject(),
                ErrorMessage = j.ErrorMessage,
                TenantId = j.TenantId,
                CreatedAt = PostgresInstanceTransaction.Utc(j.CreatedAt),
            };
        }
    }

    private static async Task LoadIncidentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EngineState state, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<IncidentRow>(new CommandDefinition(
            "SELECT key AS Key, element_instance_key AS ElementInstanceKey, job_key AS JobKey, error_type AS ErrorType, error_message AS ErrorMessage, state AS State, created_at AS CreatedAt, resolved_at AS ResolvedAt " +
            "FROM incident WHERE instance_key = @key",
            new { key = state.InstanceKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var i in rows)
        {
            state.Incidents[i.Key] = new IncidentRecord
            {
                Key = i.Key,
                InstanceKey = state.InstanceKey,
                ElementInstanceKey = i.ElementInstanceKey,
                JobKey = i.JobKey,
                ErrorType = i.ErrorType,
                ErrorMessage = i.ErrorMessage,
                State = Enum.Parse<IncidentState>(i.State, ignoreCase: true),
                CreatedAt = PostgresInstanceTransaction.Utc(i.CreatedAt),
                ResolvedAt = i.ResolvedAt is { } r ? PostgresInstanceTransaction.Utc(r) : null,
            };
        }
    }

    private sealed class ElementRow
    {
        public long Key { get; set; }

        public string ElementId { get; set; } = string.Empty;

        public string ElementType { get; set; } = string.Empty;

        public long ScopeKey { get; set; }

        public string State { get; set; } = string.Empty;

        public string? IncomingFlowId { get; set; }

        public int? LoopCounter { get; set; }

        public DateTime StartedAt { get; set; }

        public DateTime? EndedAt { get; set; }
    }

    private sealed class JobRow
    {
        public long Key { get; set; }

        public long ElementInstanceKey { get; set; }

        public string ElementId { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public int Retries { get; set; }

        public DateTime? RetryBackoffUntil { get; set; }

        public DateTime? Deadline { get; set; }

        public string? Worker { get; set; }

        public string CustomHeaders { get; set; } = "{}";

        public string Variables { get; set; } = "{}";

        public string? ResultVariables { get; set; }

        public string? ErrorMessage { get; set; }

        public string TenantId { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
    }

    private sealed class IncidentRow
    {
        public long Key { get; set; }

        public long? ElementInstanceKey { get; set; }

        public long? JobKey { get; set; }

        public string ErrorType { get; set; } = string.Empty;

        public string ErrorMessage { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public DateTime? ResolvedAt { get; set; }
    }
}

/// <summary>Turns engine effects into one batch of SQL statements, in effect order.</summary>
internal static class EffectWriter
{
    public static void AddAll(NpgsqlBatch batch, long instanceKey, string tenantId, IReadOnlyList<EngineEffect> effects, bool outboxEnabled)
    {
        // Element, job and incident snapshots are upserts: only the last snapshot per key matters.
        var lastElement = LastIndexByKey(effects, e => e is ElementChanged c ? c.Snapshot.Key : null);
        var lastJob = LastIndexByKey(effects, e => e is JobChanged c ? c.Snapshot.Key : null);
        var lastIncident = LastIndexByKey(effects, e => e is IncidentChanged c ? c.Snapshot.Key : null);
        for (var i = 0; i < effects.Count; i++)
        {
            switch (effects[i])
            {
                case InstanceStarted started:
                    InsertInstance(batch, started);
                    break;
                case InstanceStateChanged changed:
                    Add(batch, "UPDATE process_instance SET state = @s, ended_at = @e, row_version = row_version + 1 WHERE key = @k", ("s", changed.State.ToString().ToUpperInvariant()), ("e", Db(changed.EndedAt)), ("k", instanceKey));
                    break;
                case ElementChanged element when lastElement[element.Snapshot.Key] == i:
                    UpsertElement(batch, instanceKey, element.Snapshot);
                    break;
                case JobChanged job when lastJob[job.Snapshot.Key] == i:
                    UpsertJob(batch, job.Snapshot);
                    break;
                case IncidentChanged incident when lastIncident[incident.Snapshot.Key] == i:
                    UpsertIncident(batch, incident.Snapshot);
                    break;
                case VariableWritten variable:
                    UpsertVariable(batch, instanceKey, variable);
                    break;
                case ScopeVariablesCleared cleared:
                    Add(batch, "DELETE FROM variable WHERE scope_key = @s", ("s", cleared.Scope));
                    break;
                case HistoryAppended history:
                    InsertHistory(batch, instanceKey, history);
                    if (outboxEnabled && OutboxEvents.Contains(history.EventType))
                    {
                        InsertOutbox(batch, instanceKey, tenantId, history);
                    }

                    break;
                case UserTaskChanged userTask:
                    UserTaskRows.Write(batch, userTask.Snapshot);
                    break;
                case SignalThrown or MessageThrown or ChildInstanceRequested or ChildInstanceEnded or ChildErrorThrown or ChildCancelRequested:
                    break; // handled after the commit, nothing to store
                case TimerChanged or SubscriptionChanged or SubscriptionClosed or MessageCorrelated:
                    TriggerRows.Write(batch, effects[i]);
                    break;
                case ElementChanged or JobChanged or IncidentChanged:
                    break; // superseded by a later snapshot of the same key
            }
        }
    }

    private static Dictionary<long, int> LastIndexByKey(IReadOnlyList<EngineEffect> effects, Func<EngineEffect, long?> keyOf)
    {
        var result = new Dictionary<long, int>();
        for (var i = 0; i < effects.Count; i++)
        {
            if (keyOf(effects[i]) is { } key)
            {
                result[key] = i;
            }
        }

        return result;
    }

    /// <summary>History events that are also published to webhooks (FR-HIS-002, FR-ARCH-005).</summary>
    private static readonly HashSet<string> OutboxEvents = new(StringComparer.Ordinal)
    {
        HistoryEventTypes.InstanceStarted,
        HistoryEventTypes.InstanceCompleted,
        HistoryEventTypes.InstanceTerminated,
        HistoryEventTypes.IncidentCreated,
        HistoryEventTypes.IncidentResolved,
        HistoryEventTypes.JobCreated,
        HistoryEventTypes.JobCompleted,
    };

    private static void InsertOutbox(NpgsqlBatch batch, long instanceKey, string tenantId, HistoryAppended h)
    {
        var payload = new JsonObject
        {
            ["type"] = h.EventType,
            ["processInstanceKey"] = instanceKey.ToString(CultureInfo.InvariantCulture),
            ["elementId"] = h.ElementId,
            ["elementInstanceKey"] = h.ElementInstanceKey?.ToString(CultureInfo.InvariantCulture),
            ["data"] = h.Payload?.DeepClone(),
            ["occurredAt"] = h.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["tenantId"] = tenantId,
        };
        var command = Add(batch, "INSERT INTO outbox (topic, payload, created_at, tenant_id) VALUES (@topic, @payload, @created, @tenant)", ("topic", h.EventType), ("created", h.OccurredAt.UtcDateTime), ("tenant", tenantId));
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload.ToJsonString() });
    }

    private static void InsertInstance(NpgsqlBatch batch, InstanceStarted s) =>
        Add(
            batch,
            "INSERT INTO process_instance (key, definition_key, state, business_key, tenant_id, started_at, parent_instance_key, parent_element_instance_key) VALUES (@k, @d, 'ACTIVE', @b, @t, @a, @pi, @pe)",
            ("k", s.InstanceKey), ("d", s.DefinitionKey), ("b", (object?)s.BusinessKey ?? DBNull.Value), ("t", s.TenantId), ("a", s.StartedAt.UtcDateTime), ("pi", (object?)s.ParentInstanceKey ?? DBNull.Value), ("pe", (object?)s.ParentElementInstanceKey ?? DBNull.Value));

    private static void UpsertElement(NpgsqlBatch batch, long instanceKey, ElementInstance e) =>
        Add(
            batch,
            "INSERT INTO element_instance (key, instance_key, scope_key, element_id, element_type, state, incoming_flow_id, loop_counter, started_at, ended_at) " +
            "VALUES (@k, @i, @s, @id, @type, @state, @flow, @loop, @started, @ended) " +
            "ON CONFLICT (key) DO UPDATE SET state = EXCLUDED.state, ended_at = EXCLUDED.ended_at",
            ("k", e.Key), ("i", instanceKey), ("s", e.ScopeKey), ("id", e.ElementId), ("type", e.ElementType), ("state", e.State.ToString().ToUpperInvariant()),
            ("flow", (object?)e.IncomingFlowId ?? DBNull.Value), ("loop", (object?)e.LoopCounter ?? DBNull.Value), ("started", e.StartedAt.UtcDateTime), ("ended", Db(e.EndedAt)));

    private static void UpsertJob(NpgsqlBatch batch, JobRecord j)
    {
        var command = Add(
            batch,
            "INSERT INTO job (key, instance_key, element_instance_key, element_id, type, state, retries, retry_backoff_until, deadline, worker, custom_headers, variables, result_variables, error_message, tenant_id, created_at) " +
            "VALUES (@k, @i, @e, @eid, @type, @state, @retries, @backoff, @deadline, @worker, @headers, @vars, @result, @error, @tenant, @created) " +
            "ON CONFLICT (key) DO UPDATE SET state = EXCLUDED.state, retries = EXCLUDED.retries, retry_backoff_until = EXCLUDED.retry_backoff_until, " +
            "deadline = EXCLUDED.deadline, worker = EXCLUDED.worker, result_variables = EXCLUDED.result_variables, error_message = EXCLUDED.error_message",
            ("k", j.Key), ("i", j.InstanceKey), ("e", j.ElementInstanceKey), ("eid", j.ElementId), ("type", j.Type), ("state", JobStateText(j.State)),
            ("retries", j.Retries), ("backoff", Db(j.RetryBackoffUntil)), ("deadline", Db(j.Deadline)), ("worker", (object?)j.Worker ?? DBNull.Value),
            ("error", (object?)j.ErrorMessage ?? DBNull.Value), ("tenant", j.TenantId), ("created", j.CreatedAt.UtcDateTime));
        command.Parameters.Add(Json("headers", new JsonObject(j.CustomHeaders.Select(h => KeyValuePair.Create<string, JsonNode?>(h.Key, h.Value)))));
        command.Parameters.Add(Json("vars", j.Variables));
        command.Parameters.Add(Json("result", j.ResultVariables));
    }

    private static void UpsertIncident(NpgsqlBatch batch, IncidentRecord i) =>
        Add(
            batch,
            "INSERT INTO incident (key, instance_key, element_instance_key, job_key, error_type, error_message, state, created_at, resolved_at) " +
            "VALUES (@k, @i, @e, @j, @type, @msg, @state, @created, @resolved) " +
            "ON CONFLICT (key) DO UPDATE SET state = EXCLUDED.state, resolved_at = EXCLUDED.resolved_at",
            ("k", i.Key), ("i", i.InstanceKey), ("e", (object?)i.ElementInstanceKey ?? DBNull.Value), ("j", (object?)i.JobKey ?? DBNull.Value),
            ("type", i.ErrorType), ("msg", i.ErrorMessage), ("state", i.State.ToString().ToUpperInvariant()), ("created", i.CreatedAt.UtcDateTime), ("resolved", Db(i.ResolvedAt)));

    private static void UpsertVariable(NpgsqlBatch batch, long instanceKey, VariableWritten v)
    {
        var command = Add(
            batch,
            "INSERT INTO variable (instance_key, scope_key, name, value, updated_at) VALUES (@i, @s, @n, @v, now()) " +
            "ON CONFLICT (scope_key, name) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at",
            ("i", instanceKey), ("s", v.Scope), ("n", v.Name));
        command.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = v.Value is null ? "null" : v.Value.ToJsonString() });
    }

    private static void InsertHistory(NpgsqlBatch batch, long instanceKey, HistoryAppended h)
    {
        var command = Add(
            batch,
            "INSERT INTO history_event (instance_key, element_instance_key, event_type, element_id, payload, occurred_at) VALUES (@i, @e, @type, @eid, @p, @at)",
            ("i", instanceKey), ("e", (object?)h.ElementInstanceKey ?? DBNull.Value), ("type", h.EventType), ("eid", (object?)h.ElementId ?? DBNull.Value), ("at", h.OccurredAt.UtcDateTime));
        command.Parameters.Add(Json("p", h.Payload ?? []));
    }

    private static NpgsqlBatchCommand Add(NpgsqlBatch batch, string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value));
        }

        batch.BatchCommands.Add(command);
        return command;
    }

    private static NpgsqlParameter Json(string name, JsonObject? value) =>
        new(name, NpgsqlDbType.Jsonb) { Value = value is null ? DBNull.Value : value.ToJsonString() };

    private static object Db(DateTimeOffset? value) => value is { } v ? v.UtcDateTime : DBNull.Value;

    private static string JobStateText(JobState state) => state switch
    {
        JobState.Activatable => "ACTIVATABLE",
        JobState.Activated => "ACTIVATED",
        JobState.Completed => "COMPLETED",
        JobState.Failed => "FAILED",
        JobState.Canceled => "CANCELED",
        JobState.ErrorThrown => "ERROR_THROWN",
        _ => throw new InvalidOperationException($"Unknown job state {state}."), // non-union: enum
    };

    public static string Id(long key) => key.ToString(CultureInfo.InvariantCulture);
}
