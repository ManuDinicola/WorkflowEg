using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Evaluation;

/// <summary>More of the standard and Camunda FEEL functions: statistics, string and list helpers, JSON (FR-EXPR-013). Pure and bounded.</summary>
internal static class MoreBuiltins
{
    public static IEnumerable<KeyValuePair<string, BuiltinFunction>> All { get; } = new Dictionary<string, BuiltinFunction>(StringComparer.Ordinal)
    {
        ["stddev"] = (a, _) => StdDev(Builtins.Numbers(a)),
        ["variance"] = (a, _) => Variance(Builtins.Numbers(a)),
        ["mode"] = (a, _) => Mode(Builtins.Numbers(a)),
        ["trim"] = (a, _) => Builtins.Text(a, 0).Trim(),
        ["is blank"] = (a, _) => string.IsNullOrWhiteSpace(Builtins.Text(a, 0)),
        ["is empty"] = (a, _) => Builtins.Arg(a, 0) is ListValue { Items.Count: 0 },
        ["decimal"] = (a, _) => Math.Round(Builtins.Number(a, 0), (int)Builtins.Number(a, 1), MidpointRounding.ToEven),
        ["list replace"] = (a, _) => ListReplace(Builtins.List(a, 0), (int)Builtins.Number(a, 1), Builtins.Arg(a, 2)),
        ["get entries"] = (a, _) => GetEntries(Builtins.Arg(a, 0)),
        ["week of year"] = (a, _) => ISOWeek.GetWeekOfYear(Instant(a).UtcDateTime),
        ["to json"] = (a, _) => ExpressionJson.ToJson(Builtins.Arg(a, 0))?.ToJsonString() ?? "null",
        ["from json"] = (a, _) => FromJson(Builtins.Text(a, 0)),
        ["to base64"] = (a, _) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Builtins.Text(a, 0))),
    };

    private static DateTimeOffset Instant(IReadOnlyList<ExpressionValue> args) =>
        Builtins.Arg(args, 0) is DateTimeOffset date ? date : throw Builtins.TypeError("A date or date and time is required.");

    private static ExpressionValue Variance(List<decimal> numbers)
    {
        if (numbers.Count < 2)
        {
            return Operators.Null;
        }

        var mean = numbers.Sum() / numbers.Count;
        return numbers.Sum(n => (n - mean) * (n - mean)) / (numbers.Count - 1);
    }

    private static ExpressionValue StdDev(List<decimal> numbers) =>
        Variance(numbers) is decimal variance ? (decimal)Math.Sqrt((double)variance) : Operators.Null;

    private static ExpressionValue Mode(List<decimal> numbers) =>
        new ListValue([.. numbers.GroupBy(n => n).GroupBy(g => g.Count()).OrderByDescending(g => g.Key).Take(1).SelectMany(g => g.Select(x => x.Key)).Order().Select(n => (ExpressionValue)n)]);

    private static ExpressionValue ListReplace(IReadOnlyList<ExpressionValue> items, int position, ExpressionValue value)
    {
        if (position < 1 || position > items.Count)
        {
            return Operators.Null;
        }

        var copy = items.ToList();
        copy[position - 1] = value;
        return new ListValue(copy);
    }

    private static ExpressionValue GetEntries(ExpressionValue value) =>
        value is ContextValue context
            ? new ListValue([.. context.Entries.Select(e => (ExpressionValue)new ContextValue(new Dictionary<string, ExpressionValue>(StringComparer.Ordinal) { ["key"] = e.Key, ["value"] = e.Value }))])
            : throw Builtins.TypeError("get entries needs a context.");

    private static ExpressionValue FromJson(string text)
    {
        try
        {
            return ExpressionJson.FromJson(JsonNode.Parse(text));
        }
        catch (JsonException)
        {
            return Operators.Null;
        }
    }
}
