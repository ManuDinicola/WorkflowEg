using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flowd.Engine.Results;

namespace Flowd.Application.Forms;

/// <summary>
/// Checks the variables submitted with a user task against its form (FR-UT-005). Understands the common components of the
/// Camunda form schema: <c>textfield</c>, <c>textarea</c>, <c>number</c>, <c>checkbox</c>, <c>select</c>, <c>radio</c>,
/// <c>checklist</c>, <c>taglist</c>, with <c>validate</c> rules <c>required</c>, <c>min</c>, <c>max</c>, <c>minLength</c>,
/// <c>maxLength</c> and <c>pattern</c>. Unknown component types are not validated. Layout components (groups) are searched recursively.
/// </summary>
public static class FormValidator
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<FailureDetail> Validate(JsonObject schema, JsonObject? variables)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var problems = new List<FailureDetail>();
        foreach (var component in Components(schema["components"]))
        {
            if (component["key"]?.GetValue<string>() is { Length: > 0 } key)
            {
                Check(component, key, variables?[key], problems);
            }
        }

        return problems;
    }

    private static IEnumerable<JsonObject> Components(JsonNode? node)
    {
        foreach (var item in (node as JsonArray) ?? [])
        {
            if (item is not JsonObject component)
            {
                continue;
            }

            yield return component;
            foreach (var nested in Components(component["components"]))
            {
                yield return nested;
            }
        }
    }

    private static void Check(JsonObject component, string key, JsonNode? value, List<FailureDetail> problems)
    {
        var rules = component["validate"] as JsonObject;
        var missing = value is null || (value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length == 0);
        if (missing)
        {
            if (rules?["required"]?.GetValue<bool>() == true)
            {
                problems.Add(new FailureDetail(key, "FORM_REQUIRED", $"'{key}' is required."));
            }

            return;
        }

        switch (component["type"]?.GetValue<string>())
        {
            case "number":
                CheckNumber(key, value, rules, problems);
                break;
            case "textfield" or "textarea":
                CheckText(key, value, rules, problems);
                break;
            case "checkbox":
                Expect(value is JsonValue b && b.TryGetValue<bool>(out _), key, "a boolean", problems);
                break;
            case "checklist" or "taglist":
                Expect(value is JsonArray, key, "a list", problems);
                break;
            case "select" or "radio":
                Expect(value is JsonValue s && s.TryGetValue<string>(out _) || value is JsonValue, key, "a single value", problems);
                break;
        }
    }

    private static void CheckNumber(string key, JsonNode? value, JsonObject? rules, List<FailureDetail> problems)
    {
        if (value is not JsonValue json || !json.TryGetValue<decimal>(out var number))
        {
            problems.Add(new FailureDetail(key, "FORM_TYPE", $"'{key}' must be a number."));
            return;
        }

        if (rules?["min"] is { } min && number < min.GetValue<decimal>())
        {
            problems.Add(new FailureDetail(key, "FORM_MIN", $"'{key}' must be at least {min}."));
        }

        if (rules?["max"] is { } max && number > max.GetValue<decimal>())
        {
            problems.Add(new FailureDetail(key, "FORM_MAX", $"'{key}' must be at most {max}."));
        }
    }

    private static void CheckText(string key, JsonNode? value, JsonObject? rules, List<FailureDetail> problems)
    {
        if (value is not JsonValue json || !json.TryGetValue<string>(out var text))
        {
            problems.Add(new FailureDetail(key, "FORM_TYPE", $"'{key}' must be text."));
            return;
        }

        if (rules?["minLength"] is { } minLength && text.Length < minLength.GetValue<int>())
        {
            problems.Add(new FailureDetail(key, "FORM_MIN_LENGTH", $"'{key}' must have at least {minLength} characters."));
        }

        if (rules?["maxLength"] is { } maxLength && text.Length > maxLength.GetValue<int>())
        {
            problems.Add(new FailureDetail(key, "FORM_MAX_LENGTH", $"'{key}' must have at most {maxLength} characters."));
        }

        if (rules?["pattern"]?.GetValue<string>() is { Length: > 0 } pattern && !Matches(text, pattern))
        {
            problems.Add(new FailureDetail(key, "FORM_PATTERN", $"'{key}' does not match the required pattern."));
        }
    }

    private static bool Matches(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.None, PatternTimeout);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
        {
            return false; // a broken or too slow pattern never accepts the value
        }
    }

    private static void Expect(bool condition, string key, string expected, List<FailureDetail> problems)
    {
        if (!condition)
        {
            problems.Add(new FailureDetail(key, "FORM_TYPE", $"'{key}' must be {expected}."));
        }
    }
}
