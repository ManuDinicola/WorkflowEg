using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Flowd.Application.Connectors;

/// <summary>
/// Fills <c>{{name}}</c> placeholders from the job variables (dotted paths such as <c>order.id</c>) and <c>{{secrets.NAME}}</c> from the
/// secret provider. A placeholder that cannot be resolved is an error, never an empty string.
/// </summary>
public static partial class Template
{
    public static string Render(string template, JsonObject variables, ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(secrets);
        return Placeholder().Replace(template, match => Resolve(match.Groups["name"].Value, variables, secrets));
    }

    private static string Resolve(string name, JsonObject variables, ISecretProvider secrets)
    {
        if (name.StartsWith("secrets.", StringComparison.Ordinal))
        {
            return secrets.Find(name["secrets.".Length..]) ?? throw new ConnectorException($"Secret '{name["secrets.".Length..]}' is not configured.");
        }

        JsonNode? node = variables;
        foreach (var part in name.Split('.'))
        {
            node = (node as JsonObject)?[part];
        }

        return node switch
        {
            null => throw new ConnectorException($"Variable '{name}' is not defined."),
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => node.ToJsonString(), // non-union: JSON kind
        };
    }

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z0-9_.\-]+)\s*\}\}", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex Placeholder();
}
