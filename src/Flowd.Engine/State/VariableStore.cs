using System.Text.Json.Nodes;

namespace Flowd.Engine.State;

/// <summary>
/// Scoped variables (section 8.3). Lookup walks from a scope up to the root; writes go to the scope that already defines the
/// variable, else to the scope they were set in (FR-VAR-001).
/// </summary>
public sealed class VariableStore(Func<long, long?> parentOf)
{
    private readonly Dictionary<long, Dictionary<string, JsonNode?>> _scopes = [];
    private readonly Dictionary<(long Scope, string Name), int> _sizes = [];

    public int TotalBytes => _sizes.Values.Sum();

    /// <summary>All variables of a single scope.</summary>
    public IReadOnlyDictionary<string, JsonNode?> InScope(long scope) =>
        _scopes.TryGetValue(scope, out var variables) ? variables : new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

    /// <summary>Every variable visible from <paramref name="scope"/>; inner scopes shadow outer ones.</summary>
    public JsonObject Visible(long scope)
    {
        var chain = new List<long>();
        for (long? current = scope; current is { } key; current = parentOf(key))
        {
            chain.Add(key);
        }

        var result = new JsonObject();
        foreach (var key in Enumerable.Reverse(chain))
        {
            foreach (var (name, value) in InScope(key))
            {
                result[name] = value?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>The scope that defines <paramref name="name"/> when looked up from <paramref name="scope"/>, or null.</summary>
    public long? DefiningScope(long scope, string name)
    {
        for (long? current = scope; current is { } key; current = parentOf(key))
        {
            if (InScope(key).ContainsKey(name))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>Writes a variable into exactly <paramref name="scope"/>.</summary>
    public void SetIn(long scope, string name, JsonNode? value)
    {
        if (!_scopes.TryGetValue(scope, out var variables))
        {
            variables = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            _scopes[scope] = variables;
        }

        variables[name] = value?.DeepClone();
        _sizes[(scope, name)] = SizeOf(value);
    }

    /// <summary>Set-in-defining-scope write (FR-VAR-001); returns the scope that was written.</summary>
    public long Propagate(long fromScope, string name, JsonNode? value)
    {
        var target = DefiningScope(fromScope, name) ?? fromScope;
        SetIn(target, name, value);
        return target;
    }

    /// <summary>Removes all variables of a scope; returns the removed names.</summary>
    public IReadOnlyList<string> RemoveScope(long scope)
    {
        if (!_scopes.Remove(scope, out var removed))
        {
            return [];
        }

        foreach (var name in removed.Keys)
        {
            _sizes.Remove((scope, name));
        }

        return [.. removed.Keys];
    }

    public static int SizeOf(JsonNode? value) => value is null ? 4 : value.ToJsonString().Length;

    public VariableStore CloneWith(Func<long, long?> newParentOf)
    {
        var copy = new VariableStore(newParentOf);
        foreach (var (scope, variables) in _scopes)
        {
            foreach (var (name, value) in variables)
            {
                copy.SetIn(scope, name, value);
            }
        }

        return copy;
    }

    public IEnumerable<(long Scope, string Name, JsonNode? Value)> All() =>
        _scopes.SelectMany(s => s.Value.Select(v => (s.Key, v.Key, v.Value)));
}
