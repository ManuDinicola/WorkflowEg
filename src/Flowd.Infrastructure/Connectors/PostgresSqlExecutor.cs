using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowd.Application.Connectors;
using Npgsql;

namespace Flowd.Infrastructure.Connectors;

/// <summary>Runs SQL connector statements on the configured PostgreSQL connections: bound parameters only, a read-only transaction unless allowed otherwise, a row limit and a time limit (FR-CON-017).</summary>
public sealed class PostgresSqlExecutor(ConnectorOptions options) : ISqlExecutor
{
    public async Task<SqlResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var settings = options.Sql.Connections[statement.Connection];
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (settings.ReadOnly)
        {
            await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

#pragma warning disable CA2100 // the statement text is written by the process author in the BPMN file; values from variables are only ever bound parameters
        await using var command = new NpgsqlCommand(statement.Text, connection, transaction) { CommandTimeout = Math.Max(options.TimeoutSeconds, 1) };
#pragma warning restore CA2100
        foreach (var (name, value) in statement.Parameters)
        {
            command.Parameters.AddWithValue(name, Value(value));
        }

        if (!statement.ReturnsRows)
        {
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqlResult([], affected);
        }

        var rows = new List<JsonObject>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (rows.Count < statement.MaxRows && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = ToJson(await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false) ? null : reader.GetValue(i));
                }

                rows.Add(row);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SqlResult(rows, 0);
    }

    private static object Value(JsonNode? node) => node switch
    {
        null => DBNull.Value,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        _ => node.ToJsonString(), // non-union: objects and arrays are passed as JSON text
    };

    private static JsonValue? ToJson(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        short or int or long or byte => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        float or double or decimal => JsonValue.Create(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
        DateTime t => JsonValue.Create(new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset t => JsonValue.Create(t.ToString("O", CultureInfo.InvariantCulture)),
        Guid g => JsonValue.Create(g.ToString()),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)), // non-union: any other database type as text
    };
}
