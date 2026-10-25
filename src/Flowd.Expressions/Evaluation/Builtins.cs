using System.Globalization;
using System.Xml;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Evaluation;

/// <summary>The built-in function library (section 8.2). Functions are pure; <c>now()</c> reads the injected instant.</summary>
internal delegate ExpressionValue BuiltinFunction(IReadOnlyList<ExpressionValue> args, DateTimeOffset now);

internal static partial class Builtins
{


    private static readonly Dictionary<string, BuiltinFunction> CoreTable = new(StringComparer.Ordinal)
    {
        ["count"] = (a, _) => (decimal)List(a, 0).Count,
        ["sum"] = (a, _) => Numbers(a).Aggregate(0m, (x, y) => x + y),
        ["min"] = (a, _) => Numbers(a).DefaultIfEmpty().Min() is var m && Numbers(a).Count == 0 ? Operators.Null : m,
        ["max"] = (a, _) => Numbers(a).DefaultIfEmpty().Max() is var m && Numbers(a).Count == 0 ? Operators.Null : m,
        ["append"] = (a, _) => new ListValue([.. List(a, 0), .. a.Skip(1)]),
        ["contains"] = (a, _) => Text(a, 0).Contains(Text(a, 1), StringComparison.Ordinal),
        ["starts with"] = (a, _) => Text(a, 0).StartsWith(Text(a, 1), StringComparison.Ordinal),
        ["ends with"] = (a, _) => Text(a, 0).EndsWith(Text(a, 1), StringComparison.Ordinal),
        ["upper case"] = (a, _) => Text(a, 0).ToUpperInvariant(),
        ["lower case"] = (a, _) => Text(a, 0).ToLowerInvariant(),
        ["string length"] = (a, _) => (decimal)Text(a, 0).Length,
        ["substring"] = (a, _) => Substring(a),
        ["number"] = (a, _) => ParseNumber(Text(a, 0)),
        ["string"] = (a, _) => ToText(Arg(a, 0)),
        ["date and time"] = (a, _) => ParseInstant(Text(a, 0)),
        ["now"] = (_, now) => now,
        ["duration"] = (a, _) => ParseDuration(Text(a, 0)),
        ["time"] = (a, _) => ToTime(a),
        ["years and months duration"] = (a, _) => MonthsBetween(a),
        ["abs"] = (a, _) => Math.Abs(Number(a, 0)),
        ["floor"] = (a, _) => Math.Floor(Number(a, 0)),
        ["ceiling"] = (a, _) => Math.Ceiling(Number(a, 0)),
        ["round"] = (a, _) => Math.Round(Number(a, 0), a.Count > 1 ? (int)Number(a, 1) : 0),
        ["not"] = (a, _) => Arg(a, 0) is bool b ? !b : Operators.Null,
        ["is defined"] = (a, _) => Arg(a, 0) is not NullValue,
    };

    private static readonly Dictionary<string, BuiltinFunction> Table = CoreTable.Concat(ExtendedBuiltins.All).Concat(MoreBuiltins.All).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    public static bool Exists(string name) => Table.ContainsKey(name);

    public static ExpressionValue Call(string name, IReadOnlyList<ExpressionValue> args, DateTimeOffset now) =>
        Table.TryGetValue(name, out var function)
            ? function(args, now)
            : throw new EvaluationFailure(ExpressionErrorCodes.UnknownFunction, $"Unknown function '{name}'.");

    public static string ToText(ExpressionValue value) => value switch
    {
        NullValue => "null",
        bool b => b ? "true" : "false",
        decimal d => (d / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
        string s => s,
        DateTimeOffset t => t.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan span => XmlConvert.ToString(span),
        ListValue l => "[" + string.Join(", ", l.Items.Select(ToText)) + "]",
        ContextValue c => "{" + string.Join(", ", c.Entries.Select(e => $"{e.Key}: {ToText(e.Value)}")) + "}",
        FunctionValue f => $"function({string.Join(", ", f.Parameters)})",
        TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        YearsMonthsDuration months => MonthsText(months),
    };

    internal static ExpressionValue Arg(IReadOnlyList<ExpressionValue> args, int index) =>
        index < args.Count ? args[index] : throw TypeError($"Missing argument {index + 1}.");

    internal static string Text(IReadOnlyList<ExpressionValue> args, int index) =>
        Arg(args, index) is string s ? s : throw TypeError($"Argument {index + 1} must be a string.");

    internal static decimal Number(IReadOnlyList<ExpressionValue> args, int index) =>
        Arg(args, index) is decimal d ? d : throw TypeError($"Argument {index + 1} must be a number.");

    internal static IReadOnlyList<ExpressionValue> List(IReadOnlyList<ExpressionValue> args, int index) =>
        Arg(args, index) is ListValue l ? l.Items : throw TypeError($"Argument {index + 1} must be a list.");

    /// <summary>Numbers of a single list argument, or of all arguments when called with several numbers.</summary>
    internal static List<decimal> Numbers(IReadOnlyList<ExpressionValue> args)
    {
        var source = args is [ListValue only] ? only.Items : args;
        return [.. source.Select(v => v is decimal d ? d : throw TypeError("Expected numbers."))];
    }

    private static ExpressionValue Substring(IReadOnlyList<ExpressionValue> args)
    {
        var text = Text(args, 0);
        var start = (int)Number(args, 1);
        var from = start > 0 ? start - 1 : text.Length + start;
        if (from < 0 || from > text.Length)
        {
            return Operators.Null;
        }

        var length = args.Count > 2 ? (int)Number(args, 2) : text.Length - from;
        return text.Substring(from, Math.Clamp(length, 0, text.Length - from));
    }

    private static ExpressionValue ParseNumber(string text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : Operators.Null;

    private static ExpressionValue ParseInstant(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : Operators.Null;

    private static string MonthsText(YearsMonthsDuration duration)
    {
        var months = Math.Abs(duration.Months);
        return (duration.Months < 0 ? "-P" : "P") + (months / 12 > 0 ? $"{months / 12}Y" : string.Empty) + (months % 12 > 0 || months == 0 ? $"{months % 12}M" : string.Empty);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<sign>-?)P(?:(?<y>\d+)Y)?(?:(?<m>\d+)M)?$", System.Text.RegularExpressions.RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial System.Text.RegularExpressions.Regex MonthsDuration();

    private static ExpressionValue ParseDuration(string text)
    {
        if (MonthsDuration().Match(text) is { Success: true } match && (match.Groups["y"].Success || match.Groups["m"].Success))
        {
            var total = (match.Groups["y"].Success ? int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture) * 12 : 0) + (match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0);
            return new YearsMonthsDuration(match.Groups["sign"].Value == "-" ? -total : total);
        }

        try
        {
            return XmlConvert.ToTimeSpan(text);
        }
        catch (FormatException)
        {
            return Operators.Null;
        }
    }

    private static ExpressionValue ToTime(IReadOnlyList<ExpressionValue> args) => Arg(args, 0) switch
    {
        string text when TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var parsed) => parsed,
        DateTimeOffset instant => TimeOnly.FromDateTime(instant.UtcDateTime),
        decimal hour when args.Count >= 3 => new TimeOnly((int)hour, (int)Number(args, 1), (int)Number(args, 2)),
        _ => Operators.Null, // non-union: argument kinds of time()
    };

    private static ExpressionValue MonthsBetween(IReadOnlyList<ExpressionValue> args)
    {
        if (Arg(args, 0) is not DateTimeOffset from || Arg(args, 1) is not DateTimeOffset to)
        {
            return Operators.Null;
        }

        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        if (to > from && to.Day < from.Day)
        {
            months--;
        }

        return new YearsMonthsDuration(months);
    }

    internal static EvaluationFailure TypeError(string message) => new(ExpressionErrorCodes.Type, message);
}
