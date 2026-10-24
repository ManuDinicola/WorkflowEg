using System.Net;
using System.Text;

namespace Flowd.Integration.Tests;

/// <summary>FR-DMN-004: a business rule task calls a deployed decision and stores the result.</summary>
[Trait("Category", "Integration")]
public sealed class BusinessRuleTaskTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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
    public async Task FR_DMN_004_the_decision_result_lands_in_the_result_variable_and_the_process_continues()
    {
        await using var app = await TestApp.StartAsync(postgres);
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Dmn));
        form.Add(file, "resources", "discount.dmn");
        (await app.Client.PostAsync(new Uri("/v1/decisions", UriKind.Relative), form)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await app.DeployAsync(Process);

        var key = await app.StartInstanceAsync("order", """{"total":150}""");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await app.GetInstanceAsync(key)).State != "COMPLETED")
        {
            await Task.Delay(100, timeout.Token);
        }

        (await app.GetVariablesAsync(key))["rate"]!.GetValue<decimal>().ShouldBe(10m);
    }
}
