using System.Globalization;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Tests;

/// <summary>The extended FEEL built-in library (FR-EXPR-010).</summary>
public sealed class BuiltinFunctionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static string Run(string expression)
    {
        var result = FeelEvaluator.Evaluate("= " + expression, new ExpressionContext(new Dictionary<string, ExpressionValue>(StringComparer.Ordinal), Now));
        return result is EvaluationSuccess success ? Text(success.Value) : "ERR";
    }

    private static string Text(ExpressionValue value)
    {
        if (value is ListValue list)
        {
            return "[" + string.Join(",", list.Items.Select(Text)) + "]";
        }

        if (value is decimal number)
        {
            return number.ToString("0.##", CultureInfo.InvariantCulture);
        }

        if (value is bool flag)
        {
            return flag ? "true" : "false";
        }

        if (value is string text)
        {
            return text;
        }

        return value is NullValue ? "null" : "other";
    }

    [Theory]
    [InlineData("replace(\"hello world\", \"o\", \"0\")", "hell0 w0rld")]
    [InlineData("matches(\"abc\", \"^a.c$\")", "true")]
    [InlineData("matches(\"abd\", \"^a.c$\")", "false")]
    [InlineData("split(\"a,b,c\", \",\")", "[a,b,c]")]
    [InlineData("substring before(\"foobar\", \"bar\")", "foo")]
    [InlineData("substring after(\"foobar\", \"foo\")", "bar")]
    [InlineData("string join([\"a\", \"b\"], \"-\")", "a-b")]
    public void FR_EXPR_010_string_functions(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory]
    [InlineData("concatenate([1], [2, 3])", "[1,2,3]")]
    [InlineData("reverse([1, 2, 3])", "[3,2,1]")]
    [InlineData("sort([3, 1, 2])", "[1,2,3]")]
    [InlineData("sort([\"b\", \"a\"])", "[a,b]")]
    [InlineData("index of([1, 2, 1], 1)", "[1,3]")]
    [InlineData("distinct values([1, 1, 2])", "[1,2]")]
    [InlineData("flatten([[1], [2, [3]]])", "[1,2,3]")]
    [InlineData("list contains([1, 2], 2)", "true")]
    [InlineData("remove([1, 2, 3], 2)", "[1,3]")]
    [InlineData("insert before([1, 3], 2, 2)", "[1,2,3]")]
    [InlineData("sublist([1, 2, 3, 4], 2, 2)", "[2,3]")]
    [InlineData("union([1, 2], [2, 3])", "[1,2,3]")]
    [InlineData("mean([1, 2, 3])", "2")]
    [InlineData("median([1, 2, 3, 4])", "2.5")]
    [InlineData("product([2, 3])", "6")]
    [InlineData("all([true, false])", "false")]
    [InlineData("any([true, false])", "true")]
    public void FR_EXPR_010_list_functions(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory]
    [InlineData("modulo(7, 3)", "1")]
    [InlineData("modulo(-7, 3)", "2")]
    [InlineData("modulo(7, 0)", "null")]
    [InlineData("sqrt(16)", "4")]
    [InlineData("sqrt(-1)", "null")]
    [InlineData("even(4)", "true")]
    [InlineData("odd(4)", "false")]
    [InlineData("round up(1.1)", "2")]
    [InlineData("round down(1.9)", "1")]
    [InlineData("round half up(2.5)", "3")]
    [InlineData("round half down(2.5)", "2")]
    [InlineData("round half up(1.15, 1)", "1.2")]
    public void FR_EXPR_010_number_functions(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory]
    [InlineData("year(date and time(\"2026-10-24T12:00:00Z\"))", "2026")]
    [InlineData("month(date and time(\"2026-10-24T12:00:00Z\"))", "10")]
    [InlineData("day(date and time(\"2026-10-24T12:00:00Z\"))", "24")]
    [InlineData("weekday(date and time(\"2026-10-24T12:00:00Z\"))", "6")]
    [InlineData("year(today())", "2026")]
    public void FR_EXPR_010_date_functions(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory]
    [InlineData("get value({a: 1}, \"a\")", "1")]
    [InlineData("get value({a: 1}, \"x\")", "null")]
    [InlineData("get value(context put({a: 1}, \"b\", 2), \"b\")", "2")]
    [InlineData("get value(context merge([{a: 1}, {a: 2}]), \"a\")", "2")]
    public void FR_EXPR_010_context_functions(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Fact]
    public void FR_EXPR_010_a_catastrophic_regular_expression_is_cut_off_by_the_timeout() =>
        Run("matches(\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!\", \"^(a+)+$\")").ShouldBe("ERR");

    [Theory]
    [InlineData("3 in [1..5]", "true")]
    [InlineData("1 in [1..5]", "true")]
    [InlineData("1 in (1..5)", "false")]
    [InlineData("5 in [1..5)", "false")]
    [InlineData("5 in [1..5]", "true")]
    [InlineData("6 in [1..5]", "false")]
    [InlineData("2 in [1, 2]", "true")]
    [InlineData("3 in [1, 2]", "false")]
    public void FR_EXPR_011_ranges_are_intervals_after_in_and_lists_still_work(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory]
    [InlineData("date and time(\"2026-10-24T12:00:00Z\") + duration(\"P1D\") > date and time(\"2026-10-25T00:00:00Z\")", "true")]
    [InlineData("(date and time(\"2026-10-25T12:00:00Z\") - date and time(\"2026-10-24T00:00:00Z\")).hours", "12")]
    [InlineData("(date and time(\"2026-10-25T12:00:00Z\") - date and time(\"2026-10-24T00:00:00Z\")).days", "1")]
    [InlineData("duration(\"PT1H\") * 3 = duration(\"PT3H\")", "true")]
    [InlineData("2 * duration(\"PT30M\") = duration(\"PT1H\")", "true")]
    [InlineData("duration(\"PT3H\") / 3 = duration(\"PT1H\")", "true")]
    [InlineData("duration(\"PT3H\") / duration(\"PT1H\")", "3")]
    [InlineData("duration(\"PT3H\") / 0", "null")]
    [InlineData("date and time(\"2026-10-24T13:45:30Z\").hour", "13")]
    [InlineData("date and time(\"2026-10-24T13:45:30Z\").minute", "45")]
    [InlineData("date and time(\"2026-10-24T13:45:30Z\").year", "2026")]
    [InlineData("date and time(\"2026-10-24T13:45:30Z\").weekday", "6")]
    [InlineData("date(\"2026-10-24\") = date and time(\"2026-10-24T00:00:00Z\")", "true")]
    [InlineData("date(date and time(\"2026-10-24T13:45:30Z\")) = date(\"2026-10-24\")", "true")]
    [InlineData("date(\"not a date\")", "null")]
    [InlineData("day of week(date and time(\"2026-10-24T12:00:00Z\"))", "Saturday")]
    [InlineData("month of year(date and time(\"2026-10-24T12:00:00Z\"))", "October")]
    [InlineData("last day of month(date and time(\"2028-02-10T12:00:00Z\"))", "29")]
    [InlineData("today() + duration(\"P7D\") > now()", "true")]
    public void FR_EXPR_012_date_and_duration_arithmetic_properties_and_functions(string expression, string expected) => Run(expression).ShouldBe(expected);
}
