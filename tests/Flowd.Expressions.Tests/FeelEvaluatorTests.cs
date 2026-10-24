using System.Text.Json.Nodes;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Tests;

/// <summary>T-EXPR-01..10: table-driven checks of the flowd-FEEL subset (section 8.2).</summary>
public sealed class FeelEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static ExpressionContext Context(string json = "{}")
    {
        var variables = ExpressionJson.ToVariables(JsonNode.Parse(json)!.AsObject());
        return new ExpressionContext(variables, Now);
    }

    private static string Run(string expression, string json = "{}", EvaluationBudget? budget = null) =>
        FeelEvaluator.Evaluate(expression, Context(json), budget) switch
        {
            EvaluationSuccess s => Builtins_ToText(s.Value),
            EvaluationError e => $"ERR:{e.Code}",
        };

    private static string Builtins_ToText(ExpressionValue value) => value switch
    {
        NullValue => "null",
        bool b => b ? "true" : "false",
        decimal d => (d / 1.000000000000000000000000000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture),
        string s => $"\"{s}\"",
        DateTimeOffset t => t.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString(),
        ListValue l => "[" + string.Join(",", l.Items.Select(Builtins_ToText)) + "]",
        ContextValue c => "{" + string.Join(",", c.Entries.Select(e => $"{e.Key}:{Builtins_ToText(e.Value)}")) + "}",
    };

    [Theory] // T-EXPR-01 arithmetic
    [InlineData("= 1 + 2 * 3", "7")]
    [InlineData("= (1 + 2) * 3", "9")]
    [InlineData("= 10 / 4", "2.5")]
    [InlineData("= 2 ** 3", "8")]
    [InlineData("= -5 + 2", "-3")]
    [InlineData("= 1 / 0", "null")]
    [InlineData("= \"a\" + \"b\"", "\"ab\"")]
    public void T_EXPR_01_arithmetic(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory] // T-EXPR-02 comparison, including null
    [InlineData("= 1 < 2", "true")]
    [InlineData("= 2 <= 1", "false")]
    [InlineData("= \"a\" != \"b\"", "true")]
    [InlineData("= null = null", "true")]
    [InlineData("= x = null", "true")]
    [InlineData("= x = 1", "false")]
    [InlineData("= x < 1", "null")]
    [InlineData("= true and null", "null")]
    [InlineData("= false and null", "false")]
    [InlineData("= true or null", "true")]
    [InlineData("= 5 between 1 and 10", "true")]
    [InlineData("= 3 in [1,2,3]", "true")]
    public void T_EXPR_02_comparison_and_logic(string expression, string expected) => Run(expression, "{\"x\":null}").ShouldBe(expected);

    [Theory] // T-EXPR-03 paths and filters
    [InlineData("= order.total > 100", "true")]
    [InlineData("= order.items[price > 10]", "[{name:\"b\",price:20}]")]
    [InlineData("= count(order.items[price > 0])", "2")]
    [InlineData("= order.items.name", "[\"a\",\"b\"]")]
    [InlineData("= order.missing", "null")]
    public void T_EXPR_03_paths_and_filters(string expression, string expected)
    {
        const string vars = "{\"order\":{\"total\":120.5,\"items\":[{\"name\":\"a\",\"price\":5},{\"name\":\"b\",\"price\":20}]}}";

        Run(expression, vars).ShouldBe(expected);
    }

    [Theory] // T-EXPR-04 1-based indexing
    [InlineData("= [10,20,30][1]", "10")]
    [InlineData("= [10,20,30][3]", "30")]
    [InlineData("= [10,20,30][-1]", "30")]
    [InlineData("= [10,20,30][4]", "null")]
    [InlineData("= xs[2]", "20")]
    public void T_EXPR_04_indexing_is_one_based(string expression, string expected) => Run(expression, "{\"xs\":[10,20,30]}").ShouldBe(expected);

    [Theory] // T-EXPR-05 string built-ins
    [InlineData("= upper case(\"abc\")", "\"ABC\"")]
    [InlineData("= lower case(\"ABC\")", "\"abc\"")]
    [InlineData("= string length(\"hello\")", "5")]
    [InlineData("= substring(\"hello\", 2, 3)", "\"ell\"")]
    [InlineData("= substring(\"hello\", 3)", "\"llo\"")]
    [InlineData("= contains(\"hello\", \"ell\")", "true")]
    [InlineData("= starts with(\"hello\", \"he\")", "true")]
    [InlineData("= ends with(\"hello\", \"lo\")", "true")]
    [InlineData("= string(12.50)", "\"12.5\"")]
    [InlineData("= number(\"42\") + 1", "43")]
    public void T_EXPR_05_string_builtins(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Theory] // T-EXPR-06 if/then/else and list/number built-ins
    [InlineData("= if 5 > 3 then \"big\" else \"small\"", "\"big\"")]
    [InlineData("= if null then 1 else 2", "2")]
    [InlineData("= sum([1,2,3])", "6")]
    [InlineData("= min([3,1,2])", "1")]
    [InlineData("= max(4, 9, 2)", "9")]
    [InlineData("= append([1], 2, 3)", "[1,2,3]")]
    [InlineData("= abs(-3)", "3")]
    [InlineData("= floor(2.7)", "2")]
    [InlineData("= ceiling(2.1)", "3")]
    [InlineData("= round(2.567, 2)", "2.57")]
    [InlineData("= {a: 1, b: a}", "ERR:UNDEFINED_VARIABLE")]
    [InlineData("= {a: 1, \"b\": 2}.b", "2")]
    public void T_EXPR_06_conditionals_and_numeric_builtins(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Fact] // T-EXPR-07 dates and durations
    public void T_EXPR_07_date_and_duration_parsing_and_arithmetic()
    {
        Run("= date and time(\"2026-12-01T10:00:00Z\") + duration(\"PT15M\")").ShouldBe("2026-12-01T10:15:00.0000000+00:00");
        Run("= duration(\"P1D\") = duration(\"PT24H\")").ShouldBe("true");
        Run("= now()").ShouldBe("2026-10-24T12:00:00.0000000+00:00");
        Run("= now() < date and time(\"2027-01-01T00:00:00Z\")").ShouldBe("true");
        Run("= duration(\"nonsense\")").ShouldBe("null");
    }

    [Theory] // T-EXPR-08 errors
    [InlineData("= missing + 1", "ERR:UNDEFINED_VARIABLE")]
    [InlineData("= 1 +", "ERR:SYNTAX_ERROR")]
    [InlineData("= (1", "ERR:SYNTAX_ERROR")]
    [InlineData("= \"open", "ERR:SYNTAX_ERROR")]
    [InlineData("= nosuch(1)", "ERR:UNKNOWN_FUNCTION")]
    [InlineData("= upper case(1)", "ERR:TYPE_ERROR")]
    public void T_EXPR_08_errors_are_values_with_stable_codes(string expression, string expected) => Run(expression).ShouldBe(expected);

    [Fact]
    public void T_EXPR_08_is_defined_does_not_fail_on_missing_variables()
    {
        Run("= is defined(missing)").ShouldBe("false");
        Run("= is defined(x)", "{\"x\":1}").ShouldBe("true");
    }

    [Fact] // T-EXPR-09 budget
    public void T_EXPR_09_step_budget_stops_runaway_expressions()
    {
        var big = "= " + string.Join(" + ", Enumerable.Repeat("1", 500));

        Run(big, budget: new EvaluationBudget(MaxSteps: 100)).ShouldBe("ERR:BUDGET_EXCEEDED");
        Run(big).ShouldBe("500");
    }

    [Fact] // T-EXPR-10 purity
    public void T_EXPR_10_evaluation_has_no_side_effects_and_is_deterministic()
    {
        var context = Context("{\"xs\":[1,2,3]}");

        var first = FeelEvaluator.Evaluate("= append(xs, 4)", context);
        var second = FeelEvaluator.Evaluate("= append(xs, 4)", context);

        (first switch { EvaluationSuccess s => Builtins_ToText(s.Value), EvaluationError e => e.Code }).ShouldBe("[1,2,3,4]");
        (second switch { EvaluationSuccess s => Builtins_ToText(s.Value), EvaluationError e => e.Code }).ShouldBe("[1,2,3,4]");
        Run("= xs", "{\"xs\":[1,2,3]}").ShouldBe("[1,2,3]");
    }

    [Fact]
    public void FR_EXPR_001_static_text_is_a_literal_and_dynamic_text_needs_the_equals_prefix()
    {
        Run("hello").ShouldBe("\"hello\"");
        Run("3").ShouldBe("\"3\"");
        FeelEvaluator.IsDynamic("= 1").ShouldBeTrue();
        FeelEvaluator.IsDynamic("1").ShouldBeFalse();
        FeelEvaluator.CheckSyntax("= 1 +").ShouldNotBeNull();
        FeelEvaluator.CheckSyntax("= 1 + 1").ShouldBeNull();
        FeelEvaluator.CheckSyntax("static").ShouldBeNull();
    }

    [Fact]
    public void FR_VAR_002_json_round_trips_through_expression_values()
    {
        var json = JsonNode.Parse("{\"a\":[1,2.5,\"x\",null,true],\"b\":{\"c\":1}}");

        var back = ExpressionJson.ToJson(ExpressionJson.FromJson(json));

        back!.ToJsonString().ShouldBe(json!.ToJsonString());
    }
}
