using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace Flowd.Expressions.Values;

/// <summary>Conversion between stored JSON variables and expression values (FR-VAR-002: values are valid JSON).</summary>
public static class ExpressionJson
{
    public static ExpressionValue FromJson(JsonNode? node) => node switch
    {
        null => NullValue.Instance,
        JsonArray array => new ListValue([.. array.Select(FromJson)]),
        JsonObject obj => new ContextValue(obj.ToDictionary(p => p.Key, p => FromJson(p.Value), StringComparer.Ordinal)),
        JsonValue value => FromScalar(value),
        _ => NullValue.Instance, // non-union: JsonNode is an open class hierarchy
    };

    /// <summary>Converts a JSON object (a variable map) into named expression values; null means no variables.</summary>
    public static IReadOnlyDictionary<string, ExpressionValue> ToVariables(JsonObject? variables) =>
        variables is null
            ? new Dictionary<string, ExpressionValue>(StringComparer.Ordinal)
            : variables.ToDictionary(p => p.Key, p => FromJson(p.Value), StringComparer.Ordinal);

    public static JsonNode? ToJson(ExpressionValue value) => value switch
    {
        NullValue => null,
        bool b => JsonValue.Create(b),
        decimal d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        DateTimeOffset t => JsonValue.Create(t.ToString("O", CultureInfo.InvariantCulture)),
        TimeSpan span => JsonValue.Create(XmlConvert.ToString(span)),
        ListValue list => new JsonArray([.. list.Items.Select(ToJson)]),
        ContextValue context => ToObject(context),
    };

    private static JsonObject ToObject(ContextValue context)
    {
        var obj = new JsonObject();
        foreach (var entry in context.Entries)
        {
            obj[entry.Key] = ToJson(entry.Value);
        }

        return obj;
    }

    private static ExpressionValue FromScalar(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.Number => decimal.Parse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture),
        _ => NullValue.Instance, // non-union: JsonValueKind is an enum
    };
}
