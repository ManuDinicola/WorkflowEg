using System.Text.Json;
using System.Text.Json.Nodes;
using Flowd.Application.Jobs;
using Flowd.Bpmn.Model;

namespace Flowd.Application.Connectors;

/// <summary>A statement for the SQL connector: the text and its named parameters, already evaluated.</summary>
public sealed record SqlStatement(string Connection, string Text, IReadOnlyDictionary<string, JsonNode?> Parameters, bool ReturnsRows, int MaxRows);

/// <summary>What a statement gave back: the rows as JSON objects, or the number of rows it changed.</summary>
public sealed record SqlResult(IReadOnlyList<JsonObject> Rows, int AffectedRows);

/// <summary>Port: runs a statement on a named, configured database connection (PostgreSQL).</summary>
public interface ISqlExecutor
{
    Task<SqlResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken);
}

/// <summary>Connections the SQL connector may use, bound from <c>Flowd:Connectors:Sql:Connections:{name}</c>. A process names a connection and never holds a connection string.</summary>
public sealed class SqlConnectionSettings
{
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>When true (the default) statements run in a read-only transaction, so only queries work.</summary>
    public bool ReadOnly { get; init; } = true;
}

public sealed class SqlSettings
{
    public Dictionary<string, SqlConnectionSettings> Connections { get; init; } = new(StringComparer.Ordinal);

    public int MaxRows { get; init; } = 1000;
}

/// <summary>
/// The SQL connector (FR-CON-017) for PostgreSQL: <c>&lt;flowd:connector type="sql" connection="orders" query="select id, total from orders where customer = @customer"
/// parameters='{"customer":"{{customerId}}"}' resultVariable="rows"/&gt;</c>. Values reach the statement only as bound parameters, never as text, so variable data cannot change the
/// statement. The connection comes from configuration by name and is read-only unless the configuration says otherwise. <c>mode="execute"</c> runs a
/// statement that changes data (needs a connection with <c>ReadOnly=false</c>) and returns <c>{ affectedRows }</c>; the default mode returns <c>{ rows, rowCount }</c>.
/// </summary>
public sealed class SqlConnectorHandler(ISqlExecutor executor, ISecretProvider secrets, ConnectorOptions options) : IConnectorHandler
{
    public string TaskType => ServiceTask.SqlConnectorTaskType;

    public async Task<ConnectorResult> ExecuteAsync(ActivatedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            var statement = Build(job);
            var result = await executor.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
            var resultVariable = job.CustomHeaders.GetValueOrDefault("resultVariable") ?? "sql";
            var answer = statement.ReturnsRows
                ? new JsonObject { ["rows"] = new JsonArray([.. result.Rows]), ["rowCount"] = result.Rows.Count }
                : new JsonObject { ["affectedRows"] = result.AffectedRows };
            return ConnectorResult.Ok(new JsonObject { [resultVariable] = answer });
        }
        catch (ConnectorException ex)
        {
            return ConnectorResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Data.Common.DbException)
        {
            return ConnectorResult.Failed($"The SQL statement failed: {ex.GetType().Name}.");
        }
    }

    private SqlStatement Build(ActivatedJob job)
    {
        var settings = job.CustomHeaders;
        var name = settings.GetValueOrDefault("connection") ?? throw new ConnectorException("The SQL connector needs a connection name.");
        if (!options.Sql.Connections.TryGetValue(name, out var connection) || string.IsNullOrWhiteSpace(connection.ConnectionString))
        {
            throw new ConnectorException($"Connection '{name}' is not configured in Flowd:Connectors:Sql:Connections.");
        }

        var returnsRows = !string.Equals(settings.GetValueOrDefault("mode"), "execute", StringComparison.OrdinalIgnoreCase);
        if (!returnsRows && connection.ReadOnly)
        {
            throw new ConnectorException($"Connection '{name}' is read-only; set ReadOnly to false to run statements that change data.");
        }

        var text = settings.GetValueOrDefault("query") ?? throw new ConnectorException("The SQL connector needs a query.");
        return new SqlStatement(name, text, Parameters(settings.GetValueOrDefault("parameters"), job), returnsRows, Math.Clamp(options.Sql.MaxRows, 1, 100_000));
    }

    private Dictionary<string, JsonNode?> Parameters(string? template, ActivatedJob job)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(template))
        {
            return values;
        }

        try
        {
            foreach (var (key, value) in (JsonNode.Parse(Template.Render(template, job.Variables, secrets)) as JsonObject) ?? throw new ConnectorException("The SQL parameters must be a JSON object."))
            {
                values[key] = value?.DeepClone();
            }
        }
        catch (JsonException)
        {
            throw new ConnectorException("The SQL parameters must be a JSON object.");
        }

        return values;
    }
}
