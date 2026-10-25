using System.Text;
using System.Text.Json.Nodes;
using Flowd.Expressions.Dmn;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Tests;

/// <summary>DMN decision tables, hit policies, unary tests and literal expressions (FR-DMN-001, FR-DMN-002).</summary>
public sealed class DmnTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static string Table(string policy, string inputs, string outputs, params string[] rules) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/">
          <decision id="d" name="d">
            <decisionTable hitPolicy="{policy}">
              {inputs}
              {outputs}
              {string.Join("\n", rules)}
            </decisionTable>
          </decision>
        </definitions>
        """;

    private static string Input(string expression) => $"<input><inputExpression><text>{expression}</text></inputExpression></input>";

    private static string Output(string name) => $"<output name=\"{name}\"/>";

    private static string Rule(string input, string output) => $"<rule><inputEntry><text>{input}</text></inputEntry><outputEntry><text>{output}</text></outputEntry></rule>";

    private static Decision ParseOne(string xml)
    {
        var result = DmnParser.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        return result is DmnDefinitions definitions ? definitions.Decisions[0] : throw new InvalidOperationException("DMN did not parse.");
    }

    private static string Run(string xml, string json)
    {
        var variables = ExpressionJson.ToVariables(JsonNode.Parse(json)!.AsObject());
        return DecisionEvaluator.Evaluate(ParseOne(xml), variables, Now) switch
        {
            DecisionResult result => Text(result.Value),
            DecisionFailure failure => $"ERR:{failure.Code}",
        };
    }

    private static string Text(ExpressionValue value)
    {
        if (value is ListValue list)
        {
            return "[" + string.Join(",", list.Items.Select(Text)) + "]";
        }

        if (value is decimal number)
        {
            return number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (value is string text)
        {
            return text;
        }

        return value is NullValue ? "null" : "other";
    }

    private static readonly string Tiers = Table(
        "UNIQUE",
        Input("amount"),
        Output("tier"),
        Rule("&lt; 100", "\"small\""),
        Rule("[100..1000]", "\"medium\""),
        Rule("&gt; 1000", "\"large\""));

    [Theory]
    [InlineData("""{"amount":50}""", "small")]
    [InlineData("""{"amount":100}""", "medium")]
    [InlineData("""{"amount":1000}""", "medium")]
    [InlineData("""{"amount":1001}""", "large")]
    public void FR_DMN_001_unique_table_with_comparisons_and_ranges(string json, string expected) =>
        Run(Tiers, json).ShouldBe(expected);

    [Fact]
    public void FR_DMN_001_open_ranges_exclude_their_bounds()
    {
        var xml = Table("UNIQUE", Input("n"), Output("r"), Rule("(1..5)", "\"in\""), Rule("-", "\"out\""));

        Run(xml, """{"n":1}""").ShouldBe("out"); // 1 is excluded from (1..5), so only the dash rule matches
        Run(xml, """{"n":3}""").ShouldBe("ERR:DMN_MULTIPLE_MATCHES");
    }

    [Fact]
    public void FR_DMN_001_unique_fails_when_two_rules_match_and_returns_null_when_none_do()
    {
        var xml = Table("UNIQUE", Input("n"), Output("r"), Rule("&gt; 1", "\"a\""), Rule("&gt; 2", "\"b\""));

        Run(xml, """{"n":5}""").ShouldBe("ERR:DMN_MULTIPLE_MATCHES");
        Run(xml, """{"n":0}""").ShouldBe("null");
    }

    [Fact]
    public void FR_DMN_001_first_returns_the_first_matching_rule()
    {
        var xml = Table("FIRST", Input("n"), Output("r"), Rule("&gt; 1", "\"a\""), Rule("&gt; 2", "\"b\""));

        Run(xml, """{"n":5}""").ShouldBe("a");
    }

    [Fact]
    public void FR_DMN_001_any_accepts_equal_outputs_and_rejects_conflicts()
    {
        var same = Table("ANY", Input("n"), Output("r"), Rule("&gt; 1", "\"a\""), Rule("&gt; 2", "\"a\""));
        var conflict = Table("ANY", Input("n"), Output("r"), Rule("&gt; 1", "\"a\""), Rule("&gt; 2", "\"b\""));

        Run(same, """{"n":5}""").ShouldBe("a");
        Run(conflict, """{"n":5}""").ShouldBe("ERR:DMN_ANY_CONFLICT");
    }

    [Fact]
    public void FR_DMN_001_rule_order_and_collect_return_lists()
    {
        var order = Table("RULE ORDER", Input("n"), Output("r"), Rule("&gt; 1", "\"a\""), Rule("&gt; 2", "\"b\""), Rule("&gt; 9", "\"c\""));
        var collect = Table("COLLECT", Input("n"), Output("r"), Rule("&gt; 1", "\"a\""), Rule("&gt; 2", "\"b\""));

        Run(order, """{"n":5}""").ShouldBe("[a,b]");
        Run(collect, """{"n":5}""").ShouldBe("[a,b]");
        Run(collect, """{"n":0}""").ShouldBe("[]");
    }

    [Theory]
    [InlineData("SUM", "6")]
    [InlineData("MIN", "1")]
    [InlineData("MAX", "3")]
    [InlineData("COUNT", "3")]
    public void FR_DMN_001_collect_aggregations(string aggregation, string expected)
    {
        var xml = Table("COLLECT\" aggregation=\"" + aggregation, Input("n"), Output("r"), Rule("&gt; 0", "1"), Rule("&gt; 0", "2"), Rule("&gt; 0", "3"));

        Run(xml, """{"n":5}""").ShouldBe(expected);
    }

    [Fact]
    public void FR_DMN_001_priority_and_output_order_use_the_declared_output_values()
    {
        var outputs = "<output name=\"r\"><outputValues><text>\"gold\",\"silver\",\"bronze\"</text></outputValues></output>";
        var priority = Table("PRIORITY", Input("n"), outputs, Rule("&gt; 0", "\"bronze\""), Rule("&gt; 0", "\"gold\""), Rule("&gt; 0", "\"silver\""));
        var outputOrder = Table("OUTPUT ORDER", Input("n"), outputs, Rule("&gt; 0", "\"bronze\""), Rule("&gt; 0", "\"gold\""), Rule("&gt; 0", "\"silver\""));

        Run(priority, """{"n":1}""").ShouldBe("gold");
        Run(outputOrder, """{"n":1}""").ShouldBe("[gold,silver,bronze]");
    }

    [Fact]
    public void FR_DMN_002_unary_tests_support_lists_negation_and_dash()
    {
        var xml = Table("FIRST", Input("tier"), Output("r"), Rule("\"gold\", \"silver\"", "\"vip\""), Rule("not(\"bronze\")", "\"other\""), Rule("-", "\"basic\""));

        Run(xml, """{"tier":"silver"}""").ShouldBe("vip");
        Run(xml, """{"tier":"x"}""").ShouldBe("other");
        Run(xml, """{"tier":"bronze"}""").ShouldBe("basic");
    }

    [Fact]
    public void FR_DMN_001_several_outputs_return_a_context_and_a_failing_input_is_an_error()
    {
        var xml = Table("FIRST", Input("missing_variable + 1"), Output("a") + Output("b"), Rule("-", "1"));

        Run(xml, "{}").ShouldBe("ERR:DMN_INPUT");
    }

    [Fact]
    public void FR_DMN_001_a_literal_expression_decision_is_evaluated()
    {
        const string xml = """
            <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/">
              <decision id="d"><literalExpression><text>price * 2</text></literalExpression></decision>
            </definitions>
            """;

        Run(xml, """{"price":21}""").ShouldBe("42");
    }

    [Fact]
    public void FR_DMN_001_malformed_xml_and_empty_decisions_are_rejected()
    {
        (DmnParser.Read(new MemoryStream(Encoding.UTF8.GetBytes("<definitions><decision id=\"x\"/></definitions>"))) is DmnInvalid).ShouldBeTrue();
        (DmnParser.Read(new MemoryStream(Encoding.UTF8.GetBytes("<definitions"))) is DmnInvalid).ShouldBeTrue();
    }

    private static string RunGraph(string xml, string decisionId, string json)
    {
        var read = DmnParser.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        var definitions = read is DmnDefinitions parsed ? parsed : throw new InvalidOperationException("DMN did not parse.");
        var variables = ExpressionJson.ToVariables(JsonNode.Parse(json)!.AsObject());
        return DecisionEvaluator.Evaluate(definitions.Decisions.Single(d => d.Id == decisionId), definitions, variables, Now) switch
        {
            DecisionResult result => Text(result.Value),
            DecisionFailure failure => $"ERR:{failure.Code}",
        };
    }

    private const string Graph = """
        <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/">
          <decision id="rate" name="Base rate"><literalExpression><text>if tier = "gold" then 10 else 5</text></literalExpression></decision>
          <decision id="discount" name="Discount">
            <informationRequirement><requiredDecision href="#rate"/></informationRequirement>
            <decisionTable hitPolicy="FIRST">
              <input><inputExpression><text>amount</text></inputExpression></input>
              <output name="d"/>
              <rule><inputEntry><text>&gt;= 100</text></inputEntry><outputEntry><text>rate * 2</text></outputEntry></rule>
              <rule><inputEntry><text>-</text></inputEntry><outputEntry><text>rate</text></outputEntry></rule>
            </decisionTable>
          </decision>
          <decision id="price" name="Price">
            <informationRequirement><requiredDecision href="#discount"/></informationRequirement>
            <literalExpression><text>amount - amount * discount / 100</text></literalExpression>
          </decision>
        </definitions>
        """;

    [Fact]
    public void FR_DMN_005_required_decisions_are_evaluated_first_and_visible_by_name_and_id_through_several_levels()
    {
        RunGraph(Graph, "price", """{"tier":"gold","amount":200}""").ShouldBe("160");
        RunGraph(Graph, "price", """{"tier":"basic","amount":50}""").ShouldBe("47.5");
        RunGraph(Graph, "discount", """{"tier":"gold","amount":10}""").ShouldBe("10");
    }

    [Fact]
    public void FR_DMN_005_missing_requirements_and_cycles_are_errors_not_hangs()
    {
        var missing = Graph.Replace("#rate", "#nope", StringComparison.Ordinal);
        const string Cycle = """
            <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/">
              <decision id="a"><informationRequirement><requiredDecision href="#b"/></informationRequirement><literalExpression><text>1</text></literalExpression></decision>
              <decision id="b"><informationRequirement><requiredDecision href="#a"/></informationRequirement><literalExpression><text>2</text></literalExpression></decision>
            </definitions>
            """;

        RunGraph(missing, "discount", """{"amount":1}""").ShouldBe("ERR:DMN_REQUIREMENT_MISSING");
        RunGraph(Cycle, "a", "{}").ShouldBe("ERR:DMN_CYCLE");
    }

    private const string Knowledge = """
        <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/">
          <businessKnowledgeModel id="m1" name="withTax">
            <encapsulatedLogic><formalParameter name="net"/><formalParameter name="rate"/><literalExpression><text>net + net * rate / 100</text></literalExpression></encapsulatedLogic>
          </businessKnowledgeModel>
          <businessKnowledgeModel id="m2" name="gross">
            <encapsulatedLogic><formalParameter name="x"/><literalExpression><text>withTax(x, 20)</text></literalExpression></encapsulatedLogic>
          </businessKnowledgeModel>
          <decision id="total" name="Total">
            <knowledgeRequirement><requiredKnowledge href="#m2"/></knowledgeRequirement>
            <literalExpression><text>gross(amount) + withTax(1, 10)</text></literalExpression>
          </decision>
        </definitions>
        """;

    [Fact]
    public void FR_DMN_006_business_knowledge_models_are_functions_decisions_can_call_and_models_can_call_earlier_ones()
    {
        RunGraph(Knowledge, "total", """{"amount":100}""").ShouldBe("121.1");
    }

    [Fact]
    public void FR_DMN_006_a_broken_knowledge_model_is_an_error_not_a_crash()
    {
        RunGraph(Knowledge.Replace("net + net * rate / 100", "net +", StringComparison.Ordinal), "total", """{"amount":1}""").ShouldBe("ERR:DMN_KNOWLEDGE_MODEL");
    }
}
