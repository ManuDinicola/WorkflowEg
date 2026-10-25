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

    [Theory]
    [InlineData("mode([1, 2, 2, 3])", "[2]")]
    [InlineData("variance([1, 2, 3, 4])", "1.67")]
    [InlineData("trim(\"  hi \")", "hi")]
    [InlineData("is blank(\"  \")", "true")]
    [InlineData("is empty([])", "true")]
    [InlineData("decimal(1.2345, 2)", "1.23")]
    [InlineData("list replace([1, 2, 3], 2, 9)", "[1,9,3]")]
    [InlineData("week of year(date and time(\"2028-01-10T12:00:00Z\"))", "2")]
    [InlineData("from json(\"{\\\"a\\\":[1,2]}\").a[2]", "2")]
    [InlineData("to json({a: 1})", "{\"a\":1}")]
    [InlineData("get entries({a: 1})[1].key", "a")]
    public void FR_EXPR_013_more_standard_and_camunda_functions(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory]
    [InlineData("{double: function(x) x * 2, r: double(4)}.r", "8")]
    [InlineData("{add: function(a, b) a + b, r: add(1, 2) + add(10, 20)}.r", "33")]
    [InlineData("{addk: function(x) x + 1, r: addk(1)}.r", "2")]
    [InlineData("{f: function(x) x, r: f(1, 2)}.r", "ERR")]
    public void FR_EXPR_014_user_defined_functions(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory]
    [InlineData("time(\"10:30:00\").hour", "10")]
    [InlineData("time(\"10:30:00\") < time(\"11:00:00\")", "true")]
    [InlineData("time(\"23:30:00\") + duration(\"PT1H\") = time(\"00:30:00\")", "true")]
    [InlineData("time(\"12:00:00\") - time(\"10:30:00\") = duration(\"PT1H30M\")", "true")]
    [InlineData("time(8, 5, 0).minute", "5")]
    [InlineData("duration(\"P1Y2M\").months", "2")]
    [InlineData("duration(\"P1Y2M\") + duration(\"P10M\") = duration(\"P2Y\")", "true")]
    [InlineData("duration(\"P1Y\") < duration(\"P13M\")", "true")]
    [InlineData("string(date and time(\"2028-01-31T00:00:00Z\") + duration(\"P1M\"))", "2028-02-29T00:00:00.0000000+00:00")]
    [InlineData("years and months duration(date and time(\"2020-01-15T00:00:00Z\"), date and time(\"2022-03-10T00:00:00Z\")).years", "2")]
    public void FR_EXPR_015_time_of_day_and_years_and_months_durations(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory]
    [InlineData("5 instance of number", "true")]
    [InlineData("\"a\" instance of number", "false")]
    [InlineData("[1, 2] instance of list", "true")]
    [InlineData("{a: 1} instance of context", "true")]
    [InlineData("@\"2026-10-25T10:00:00Z\" instance of date and time", "true")]
    [InlineData("@\"10:30:00\" instance of time", "true")]
    [InlineData("@\"P1Y2M\" instance of years and months duration", "true")]
    [InlineData("@\"PT1H\" instance of days and time duration", "true")]
    [InlineData("null instance of null", "true")]
    [InlineData("@\"2026-10-25\" < @\"2026-10-26\"", "true")]
    [InlineData("@\"2026-10-25\" + @\"P1D\" = @\"2026-10-26\"", "true")]
    [InlineData("@\"10:30:00\".hour", "10")]
    public void FR_EXPR_016_instance_of_and_temporal_literals(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory]
    [InlineData("sort([3, 1, 2], function(a, b) a < b)", "[1,2,3]")]
    [InlineData("sort([3, 1, 2], function(a, b) a > b)", "[3,2,1]")]
    [InlineData("sort([{n: 2}, {n: 1}], function(a, b) a.n < b.n)[1].n", "1")]
    [InlineData("before(1, 2)", "true")]
    [InlineData("after(1, 2)", "false")]
    [InlineData("day of year(@\"2026-02-01\")", "32")]
    [InlineData("date and time(@\"2026-10-25\", @\"10:30:00\") = @\"2026-10-25T10:30:00Z\"", "true")]
    [InlineData("number(\"1.000,50\", \".\", \",\")", "1000.5")]
    [InlineData("format number(1234.5, \"#,##0.00\")", "1,234.50")]
    public void FR_EXPR_017_sort_with_a_function_point_comparisons_and_date_helpers(string expression, string expected) => Run(expression).ShouldBe(expected);
}
