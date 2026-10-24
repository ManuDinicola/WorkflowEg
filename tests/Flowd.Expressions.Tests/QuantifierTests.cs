using System.Text.Json.Nodes;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Tests;

/// <summary>FEEL quantified expressions and for loops (FR-EXPR-009).</summary>
public sealed class QuantifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static ExpressionValue Run(string expression, string json = "{}") =>
        FeelEvaluator.Evaluate(expression, new ExpressionContext(ExpressionJson.ToVariables(JsonNode.Parse(json)!.AsObject()), Now)) is EvaluationSuccess success
            ? success.Value
            : throw new InvalidOperationException($"'{expression}' failed.");

    [Theory]
    [InlineData("= some x in [1,2,3] satisfies x > 2", true)]
    [InlineData("= some x in [1,2,3] satisfies x > 5", false)]
    [InlineData("= every x in [1,2,3] satisfies x > 0", true)]
    [InlineData("= every x in [1,2,3] satisfies x > 1", false)]
    [InlineData("= every x in [] satisfies x > 1", true)]
    public void FR_EXPR_009_quantifiers_return_booleans(string expression, bool expected) =>
        (Run(expression) is bool actual && actual == expected).ShouldBeTrue();

    [Fact]
    public void FR_EXPR_009_for_maps_a_list()
    {
        var value = Run("= for x in items return x * 2", """{"items":[1,2,3]}""");

        var numbers = new List<decimal>();
        if (value is ListValue list)
        {
            foreach (var item in list.Items)
            {
                numbers.Add(item is decimal d ? d : -1m);
            }
        }

        numbers.ShouldBe([2m, 4m, 6m]);
    }

    [Fact]
    public void FR_EXPR_009_a_variable_named_some_still_works()
    {
        var value = Run("= some + 1", """{"some":1}""");
        var text = "other";
        if (value is decimal d)
        {
            text = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (value is NullValue)
        {
            text = "null";
        }

        text.ShouldBe("2");
    }
}
