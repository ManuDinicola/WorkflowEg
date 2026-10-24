using System.Text.Json.Nodes;
using Dapper;
using Flowd.Application.UserTasks;
using Flowd.Engine.State;
using Npgsql;
using NpgsqlTypes;

namespace Flowd.Infrastructure.Postgres.Execution;

/// <summary>Reads and writes user task rows (section 5.1, API-011).</summary>
internal static class UserTaskRows
{
    private const string Columns =
        "key AS Key, instance_key AS InstanceKey, element_instance_key AS ElementInstanceKey, element_id AS ElementId, assignee AS Assignee, " +
        "candidate_groups AS CandidateGroups, candidate_users AS CandidateUsers, due_date AS DueDate, form_key AS FormKey, state AS State, " +
        "result_variables::text AS ResultVariables, tenant_id AS TenantId, created_at AS CreatedAt";

    public static async Task LoadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EngineState state, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM user_task WHERE instance_key = @key",
            new { key = state.InstanceKey },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var r in rows)
        {
            state.UserTasks[r.Key] = new UserTaskRecord
            {
                Key = r.Key,
                InstanceKey = r.InstanceKey,
                ElementInstanceKey = r.ElementInstanceKey,
                ElementId = r.ElementId,
                Assignee = r.Assignee,
                CandidateGroups = r.CandidateGroups,
                CandidateUsers = r.CandidateUsers,
                DueDate = r.DueDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                FormKey = r.FormKey,
                State = Enum.Parse<UserTaskState>(r.State, ignoreCase: true),
                ResultVariables = r.ResultVariables is null ? null : JsonNode.Parse(r.ResultVariables)!.AsObject(),
                TenantId = r.TenantId,
                CreatedAt = PostgresInstanceTransaction.Utc(r.CreatedAt),
            };
        }
    }

    public static void Write(NpgsqlBatch batch, UserTaskRecord t)
    {
        var command = new NpgsqlBatchCommand(
            "INSERT INTO user_task (key, instance_key, element_instance_key, element_id, assignee, candidate_groups, candidate_users, due_date, form_key, state, result_variables, tenant_id, created_at) " +
            "VALUES (@k, @i, @e, @eid, @assignee, @groups, @users, @due, @form, @state, @result, @tenant, @created) " +
            "ON CONFLICT (key) DO UPDATE SET assignee = EXCLUDED.assignee, state = EXCLUDED.state, result_variables = EXCLUDED.result_variables");
        command.Parameters.Add(new NpgsqlParameter("k", t.Key));
        command.Parameters.Add(new NpgsqlParameter("i", t.InstanceKey));
        command.Parameters.Add(new NpgsqlParameter("e", t.ElementInstanceKey));
        command.Parameters.Add(new NpgsqlParameter("eid", t.ElementId));
        command.Parameters.Add(new NpgsqlParameter("assignee", (object?)t.Assignee ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("groups", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = t.CandidateGroups.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("users", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = t.CandidateUsers.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.TimestampTz) { Value = DateTimeOffset.TryParse(t.DueDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var due) ? due.UtcDateTime : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("form", (object?)t.FormKey ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("state", t.State.ToString().ToUpperInvariant()));
        command.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb) { Value = t.ResultVariables is null ? DBNull.Value : t.ResultVariables.ToJsonString() });
        command.Parameters.Add(new NpgsqlParameter("tenant", t.TenantId));
        command.Parameters.Add(new NpgsqlParameter("created", t.CreatedAt.UtcDateTime));
        batch.BatchCommands.Add(command);
    }

    internal sealed class Row
    {
        public long Key { get; set; }

        public long InstanceKey { get; set; }

        public long ElementInstanceKey { get; set; }

        public string ElementId { get; set; } = string.Empty;

        public string? Assignee { get; set; }

        public string[] CandidateGroups { get; set; } = [];

        public string[] CandidateUsers { get; set; } = [];

        public DateTime? DueDate { get; set; }

        public string? FormKey { get; set; }

        public string State { get; set; } = string.Empty;

        public string? ResultVariables { get; set; }

        public string TenantId { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public UserTaskView ToView() => new(
            Key, InstanceKey, ElementInstanceKey, ElementId, Assignee, CandidateGroups, CandidateUsers,
            DueDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), FormKey, State, PostgresInstanceTransaction.Utc(CreatedAt));
    }

    public const string SelectColumns = Columns;
}

/// <summary>Read side for user tasks plus owner lookup (API-011).</summary>
public sealed class PostgresUserTaskQueries(NpgsqlDataSource dataSource) : IUserTaskQueries
{
    public async Task<UserTaskPage> ListAsync(string tenantId, UserTaskFilter filter, int pageSize, long? afterKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<UserTaskRows.Row>(new CommandDefinition(
            $"SELECT {UserTaskRows.SelectColumns} FROM user_task WHERE tenant_id = @tenantId AND key > @after " +
            "AND (@assignee::text IS NULL OR assignee = @assignee) AND (@state::text IS NULL OR state = @state) " +
            "AND (@group::text IS NULL OR @group = ANY (candidate_groups)) AND (@user::text IS NULL OR @user = ANY (candidate_users)) " +
            "AND (@instance::bigint IS NULL OR instance_key = @instance) ORDER BY key LIMIT @take",
            new
            {
                tenantId,
                after = afterKey ?? 0L,
                assignee = filter.Assignee,
                state = filter.State?.ToUpperInvariant(),
                group = filter.CandidateGroup,
                user = filter.CandidateUser,
                instance = filter.InstanceKey,
                take = pageSize + 1,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows[..pageSize] : rows;
        return new UserTaskPage([.. items.Select(r => r.ToView())], hasMore ? items[^1].Key : null);
    }

    public async Task<long?> InstanceOfUserTaskAsync(string tenantId, long taskKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT instance_key FROM user_task WHERE key = @taskKey AND tenant_id = @tenantId",
            new { taskKey, tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary>Signal subscription lookup (FR-SIG-001).</summary>
public sealed class PostgresSignalStore(NpgsqlDataSource dataSource) : Flowd.Application.Messaging.ISignalStore
{
    private const string Columns =
        "key AS Key, instance_key AS InstanceKey, element_instance_key AS ElementInstanceKey, definition_key AS DefinitionKey, element_id AS ElementId, " +
        "kind AS Kind, message_name AS MessageName, correlation_key AS CorrelationKey, tenant_id AS TenantId, signal AS Signal";

    public Task<IReadOnlyList<SubscriptionRecord>> FindSignalSubscriptionsAsync(string tenantId, string name, CancellationToken cancellationToken) =>
        FindAsync(start: false, tenantId, name, cancellationToken);

    public Task<IReadOnlyList<SubscriptionRecord>> FindStartSignalSubscriptionsAsync(string tenantId, string name, CancellationToken cancellationToken) =>
        FindAsync(start: true, tenantId, name, cancellationToken);

    private async Task<IReadOnlyList<SubscriptionRecord>> FindAsync(bool start, string tenantId, string name, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<TriggerRows.SubscriptionRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM message_subscription WHERE tenant_id = @tenantId AND message_name = @name AND signal AND ((@start AND kind = 'START') OR (NOT @start AND kind <> 'START')) ORDER BY key",
            new { tenantId, name, start },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => TriggerRows.ToRecord(r, null))];
    }
}
