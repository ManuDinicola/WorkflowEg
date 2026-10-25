using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>FR-DMN-007: every decision evaluation is recorded and can be searched.</summary>
[Trait("Category", "Integration")]
public sealed class DecisionHistoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Dmn = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/">
          <decision id="discount">
            <decisionTable hitPolicy="FIRST">
              <input><inputExpression><text>total</text></inputExpression></input>
              <output name="rate"/>
              <rule><inputEntry><text>&gt;= 100</text></inputEntry><outputEntry><text>10</text></outputEntry></rule>
              <rule><inputEntry><text>-</text></inputEntry><outputEntry><text>0</text></outputEntry></rule>
            </decisionTable>
          </decision>
        </definitions>
        """;

    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="order" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:businessRuleTask id="Rule"><bpmn:extensionElements><flowd:calledDecision decisionId="discount" resultVariable="rate"/></bpmn:extensionElements></bpmn:businessRuleTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Rule"/><bpmn:sequenceFlow id="f2" sourceRef="Rule" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public async Task FR_DMN_007_api_calls_and_business_rule_tasks_are_recorded_with_input_output_and_origin()
    {
        await using var app = await TestApp.StartAsync(postgres);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(Dmn)), "resources", "discount.dmn" } };
        (await app.Client.PostAsync(new Uri("/v1/decisions", UriKind.Relative), form)).EnsureSuccessStatusCode();
        await app.DeployAsync(Process);
        (await app.Client.PostAsJsonAsync(new Uri("/v1/decisions/discount/evaluate", UriKind.Relative), new { variables = new { total = 150 } })).EnsureSuccessStatusCode();
        (await app.Client.PostAsJsonAsync(new Uri("/v1/decisions/discount/evaluate", UriKind.Relative), new { variables = new { other = 1 } })).StatusCode.ShouldNotBe(System.Net.HttpStatusCode.OK); // no total: fails
        var instance = await app.StartInstanceAsync("order", """{"total":20}""");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await app.GetInstanceAsync(instance)).State != "COMPLETED")
        {
            await Task.Delay(100, timeout.Token);
        }

        var all = JsonNode.Parse(await app.Client.GetStringAsync(new Uri("/v1/decision-instances?decisionId=discount", UriKind.Relative)))!["items"]!.AsArray();
        var forInstance = JsonNode.Parse(await app.Client.GetStringAsync(new Uri($"/v1/decision-instances?processInstanceKey={instance}", UriKind.Relative)))!["items"]!.AsArray();
        var one = JsonNode.Parse(await app.Client.GetStringAsync(new Uri($"/v1/decision-instances/{all[0]!["key"]!.GetValue<string>()}", UriKind.Relative)))!;

        all.Count.ShouldBe(3);
        all.Select(i => i!["source"]!.GetValue<string>()).Order().ShouldBe(["api", "api", "task"]);
        all.Count(i => i!["error"] is not null).ShouldBe(1);
        forInstance.Count.ShouldBe(1);
        forInstance[0]!["processInstanceKey"]!.GetValue<string>().ShouldBe(instance);
        forInstance[0]!["input"]!["total"]!.GetValue<int>().ShouldBe(20);
        forInstance[0]!["output"]!.GetValue<int>().ShouldBe(0);
        one["decisionId"]!.GetValue<string>().ShouldBe("discount");
        (await app.Client.GetAsync(new Uri("/v1/decision-instances/1", UriKind.Relative))).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }
}
