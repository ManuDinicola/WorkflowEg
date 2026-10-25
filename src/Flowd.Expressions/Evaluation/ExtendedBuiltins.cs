using System.Globalization;
using System.Text.RegularExpressions;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Evaluation;

/// <summary>
/// The rest of the FEEL built-in library: strings, lists, numbers, dates and contexts (FR-EXPR-010). All functions are pure and
/// bounded: regular expressions have a match timeout so a hostile pattern cannot stall the engine.
/// </summary>
internal static class ExtendedBuiltins
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static IEnumerable<KeyValuePair<string, BuiltinFunction>> All { get; } = new Dictionary<string, BuiltinFunction>(StringComparer.Ordinal)
    {
        ["replace"] = (a, _) => Guarded(() => Regex.Replace(Builtins.Text(a, 0), Builtins.Text(a, 1), Builtins.Text(a, 2), RegexOptions.None, RegexTimeout)),
        ["matches"] = (a, _) => Guarded(() => Regex.IsMatch(Builtins.Text(a, 0), Builtins.Text(a, 1), RegexOptions.None, RegexTimeout)),
        ["split"] = (a, _) => Guarded(() => new ListValue([.. Regex.Split(Builtins.Text(a, 0), Builtins.Text(a, 1), RegexOptions.None, RegexTimeout).Select(s => (ExpressionValue)s)])),
        ["substring before"] = (a, _) => Before(Builtins.Text(a, 0), Builtins.Text(a, 1)),
        ["substring after"] = (a, _) => After(Builtins.Text(a, 0), Builtins.Text(a, 1)),
        ["string join"] = (a, _) => string.Join(a.Count > 1 ? Builtins.Text(a, 1) : string.Empty, Builtins.List(a, 0).Select(Builtins.ToText)),
        ["concatenate"] = (a, _) => new ListValue([.. a.SelectMany((_, i) => Builtins.List(a, i))]),
        ["reverse"] = (a, _) => new ListValue([.. Builtins.List(a, 0).Reverse()]),
        ["sort"] = (a, _) => new ListValue(Sorted(Builtins.List(a, 0))),
        ["index of"] = (a, _) => new ListValue([.. Builtins.List(a, 0).Select((v, i) => (v, i)).Where(x => Operators.ValuesEqual(x.v, Builtins.Arg(a, 1))).Select(x => (ExpressionValue)(decimal)(x.i + 1))]),
        ["distinct values"] = (a, _) => new ListValue(Distinct(Builtins.List(a, 0))),
        ["flatten"] = (a, _) => new ListValue(Flatten(Builtins.List(a, 0))),
        ["list contains"] = (a, _) => Builtins.List(a, 0).Any(v => Operators.ValuesEqual(v, Builtins.Arg(a, 1))),
        ["remove"] = (a, _) => Remove(Builtins.List(a, 0), (int)Builtins.Number(a, 1)),
        ["insert before"] = (a, _) => InsertBefore(Builtins.List(a, 0), (int)Builtins.Number(a, 1), Builtins.Arg(a, 2)),
        ["sublist"] = (a, _) => Sublist(Builtins.List(a, 0), (int)Builtins.Number(a, 1), a.Count > 2 ? (int)Builtins.Number(a, 2) : int.MaxValue),
        ["union"] = (a, _) => new ListValue(Distinct([.. a.SelectMany((_, i) => Builtins.List(a, i))])),
        ["mean"] = (a, _) => Mean(Builtins.Numbers(a)),
        ["median"] = (a, _) => Median(Builtins.Numbers(a)),
        ["product"] = (a, _) => Builtins.Numbers(a).Aggregate(1m, (x, y) => x * y),
        ["all"] = (a, _) => Booleans(a).All(b => b),
        ["any"] = (a, _) => Booleans(a).Any(b => b),
        ["modulo"] = (a, _) => Modulo(Builtins.Number(a, 0), Builtins.Number(a, 1)),
        ["sqrt"] = (a, _) => Builtins.Number(a, 0) < 0 ? Operators.Null : (decimal)Math.Sqrt((double)Builtins.Number(a, 0)),
        ["exp"] = (a, _) => (decimal)Math.Exp((double)Builtins.Number(a, 0)),
        ["log"] = (a, _) => Builtins.Number(a, 0) <= 0 ? Operators.Null : (decimal)Math.Log((double)Builtins.Number(a, 0)),
        ["even"] = (a, _) => Builtins.Number(a, 0) % 2 == 0,
        ["odd"] = (a, _) => Builtins.Number(a, 0) % 2 != 0,
        ["round up"] = (a, _) => RoundAway(Builtins.Number(a, 0), Scale(a)),
        ["round down"] = (a, _) => Math.Round(Builtins.Number(a, 0), Scale(a), MidpointRounding.ToZero),
        ["round half up"] = (a, _) => Math.Round(Builtins.Number(a, 0), Scale(a), MidpointRounding.AwayFromZero),
        ["round half down"] = (a, _) => RoundHalfDown(Builtins.Number(a, 0), Scale(a)),
        ["today"] = (_, now) => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
        ["date"] = (a, _) => ToDate(a),
        ["day of week"] = (a, _) => Instant(a).DayOfWeek.ToString(),
        ["month of year"] = (a, _) => System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(Instant(a).Month),
        ["last day of month"] = (a, _) => DateTime.DaysInMonth(Instant(a).Year, Instant(a).Month),
        ["year"] = (a, _) => Instant(a).Year,
        ["month"] = (a, _) => Instant(a).Month,
        ["day"] = (a, _) => Instant(a).Day,
        ["weekday"] = (a, _) => Instant(a).DayOfWeek == DayOfWeek.Sunday ? 7 : (int)Instant(a).DayOfWeek,
        ["get value"] = (a, _) => GetValue(a),
        ["context put"] = (a, _) => ContextPut(a),
        ["context merge"] = (a, _) => ContextMerge(Builtins.List(a, 0)),
    };

    /// <summary>A regular expression that runs too long (or is invalid) is an evaluation error, never a hang or a crash.</summary>
    private static ExpressionValue Guarded(Func<ExpressionValue> action)
    {
        try
        {
            return action();
        }
        catch (RegexMatchTimeoutException)
        {
            throw new EvaluationFailure(ExpressionErrorCodes.BudgetExceeded, "The regular expression took too long.");
        }
        catch (ArgumentException ex)
        {
            throw new EvaluationFailure(ExpressionErrorCodes.Type, $"Invalid regular expression: {ex.Message}");
        }
    }

    private static ExpressionValue Before(string text, string match)
    {
        var index = text.IndexOf(match, StringComparison.Ordinal);
        return index < 0 ? string.Empty : text[..index];
    }

    private static ExpressionValue After(string text, string match)
    {
        var index = text.IndexOf(match, StringComparison.Ordinal);
        return index < 0 ? string.Empty : text[(index + match.Length)..];
    }

    private static List<ExpressionValue> Sorted(IReadOnlyList<ExpressionValue> items)
    {
        if (items.All(i => i is decimal))
        {
            return [.. items.OrderBy(i => i is decimal d ? d : 0m)];
        }

        if (items.All(i => i is string))
        {
            return [.. items.OrderBy(i => i is string s ? s : string.Empty, StringComparer.Ordinal)];
        }

        throw Builtins.TypeError("sort needs a list of numbers or a list of strings.");
    }

    private static List<ExpressionValue> Distinct(IReadOnlyList<ExpressionValue> items)
    {
        var result = new List<ExpressionValue>();
        foreach (var item in items)
        {
            if (!result.Any(r => Operators.ValuesEqual(r, item)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static List<ExpressionValue> Flatten(IReadOnlyList<ExpressionValue> items)
    {
        var result = new List<ExpressionValue>();
        foreach (var item in items)
        {
            if (item is ListValue inner)
            {
                result.AddRange(Flatten(inner.Items));
            }
            else
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static ExpressionValue Remove(IReadOnlyList<ExpressionValue> items, int position)
    {
        var index = position > 0 ? position - 1 : items.Count + position;
        return index < 0 || index >= items.Count ? new ListValue(items) : new ListValue([.. items.Where((_, i) => i != index)]);
    }

    private static ExpressionValue InsertBefore(IReadOnlyList<ExpressionValue> items, int position, ExpressionValue item)
    {
        var index = position > 0 ? position - 1 : items.Count + position;
        if (index < 0 || index > items.Count)
        {
            return new ListValue(items);
        }

        var result = new List<ExpressionValue>(items);
        result.Insert(index, item);
        return new ListValue(result);
    }

    private static ExpressionValue Sublist(IReadOnlyList<ExpressionValue> items, int start, int length)
    {
        var from = start > 0 ? start - 1 : items.Count + start;
        if (from < 0 || from > items.Count)
        {
            return Operators.Null;
        }

        return new ListValue([.. items.Skip(from).Take(Math.Max(length, 0))]);
    }

    private static ExpressionValue Mean(List<decimal> numbers) => numbers.Count == 0 ? Operators.Null : numbers.Sum() / numbers.Count;

    private static ExpressionValue Median(List<decimal> numbers)
    {
        if (numbers.Count == 0)
        {
            return Operators.Null;
        }

        numbers.Sort();
        var middle = numbers.Count / 2;
        return numbers.Count % 2 == 1 ? numbers[middle] : (numbers[middle - 1] + numbers[middle]) / 2;
    }

    private static List<bool> Booleans(IReadOnlyList<ExpressionValue> args)
    {
        var source = args is [ListValue only] ? only.Items : args;
        return [.. source.Select(v => v is bool b ? b : throw Builtins.TypeError("Expected booleans."))];
    }

    private static ExpressionValue Modulo(decimal dividend, decimal divisor)
    {
        if (divisor == 0)
        {
            return Operators.Null;
        }

        var remainder = dividend % divisor;
        return remainder != 0 && (remainder < 0) != (divisor < 0) ? remainder + divisor : remainder;
    }

    private static int Scale(IReadOnlyList<ExpressionValue> args) => args.Count > 1 ? (int)Math.Clamp(Builtins.Number(args, 1), 0, 20) : 0;

    private static decimal RoundAway(decimal value, int scale)
    {
        var factor = (decimal)Math.Pow(10, scale);
        return Math.Sign(value) * Math.Ceiling(Math.Abs(value) * factor) / factor;
    }

    private static decimal RoundHalfDown(decimal value, int scale)
    {
        var factor = (decimal)Math.Pow(10, scale);
        var scaled = value * factor;
        var floor = Math.Floor(Math.Abs(scaled));
        var fraction = Math.Abs(scaled) - floor;
        var rounded = fraction > 0.5m ? floor + 1 : floor;
        return Math.Sign(scaled) * rounded / factor;
    }

    /// <summary><c>date("2026-10-24")</c> gives midnight UTC of that day; <c>date(date and time)</c> cuts the time off.</summary>
    private static ExpressionValue ToDate(IReadOnlyList<ExpressionValue> args)
    {
        if (Builtins.Arg(args, 0) is DateTimeOffset instant)
        {
            return new DateTimeOffset(instant.UtcDateTime.Date, TimeSpan.Zero);
        }

        return DateTime.TryParseExact(Builtins.Text(args, 0), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var day)
            ? new DateTimeOffset(day, TimeSpan.Zero)
            : Operators.Null;
    }

    private static DateTimeOffset Instant(IReadOnlyList<ExpressionValue> args) =>
        Builtins.Arg(args, 0) is DateTimeOffset t ? t : throw Builtins.TypeError("Argument 1 must be a date and time.");

    private static ExpressionValue GetValue(IReadOnlyList<ExpressionValue> args) =>
        Builtins.Arg(args, 0) is ContextValue context && context.Entries.TryGetValue(Builtins.Text(args, 1), out var value) ? value : Operators.Null;

    private static ExpressionValue ContextPut(IReadOnlyList<ExpressionValue> args)
    {
        if (Builtins.Arg(args, 0) is not ContextValue context)
        {
            throw Builtins.TypeError("Argument 1 must be a context.");
        }

        var entries = new Dictionary<string, ExpressionValue>(context.Entries, StringComparer.Ordinal) { [Builtins.Text(args, 1)] = Builtins.Arg(args, 2) };
        return new ContextValue(entries);
    }

    private static ExpressionValue ContextMerge(IReadOnlyList<ExpressionValue> contexts)
    {
        var entries = new Dictionary<string, ExpressionValue>(StringComparer.Ordinal);
        foreach (var item in contexts)
        {
            if (item is not ContextValue context)
            {
                throw Builtins.TypeError("context merge needs a list of contexts.");
            }

            foreach (var entry in context.Entries)
            {
                entries[entry.Key] = entry.Value;
            }
        }

        return new ContextValue(entries);
    }
}
