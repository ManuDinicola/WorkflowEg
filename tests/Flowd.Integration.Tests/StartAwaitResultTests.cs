using System.Text;
using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>FR-API-021: start an instance and wait for its result.</summary>
[Trait("Category", "Integration")]
public sealed class StartAwaitResultTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Instant = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="instant" isExecutable="true"><bpmn:startEvent id="S"/><bpmn:endEvent id="E"/><bpmn:sequenceFlow id="f" sourceRef="S" targetRef="E"/></bpmn:process>
        </bpmn:definitions>
        """;

    private const string Waiting = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="waiting" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="T"><bpmn:extensionElements><flowd:taskDefinition type="slow"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="T"/><bpmn:sequenceFlow id="f2" sourceRef="T" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<JsonNode> StartAsync(TestApp app, string json)
    {
        var response = await app.Client.PostAsync(new Uri("/v1/process-instances", UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task FR_API_021_a_finished_instance_returns_its_state_and_the_requested_variables()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Instant);

        var reply = await StartAsync(app, """{"bpmnProcessId":"instant","variables":{"a":1,"b":2},"awaitResult":true,"fetchVariables":["b"]}""");

        reply["state"]!.GetValue<string>().ShouldBe("COMPLETED");
        reply["variables"]!.AsObject().Select(p => p.Key).ShouldBe(["b"]);
    }

    [Fact]
    public async Task FR_API_021_an_instance_that_is_still_running_returns_after_the_wait_without_variables()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Waiting);

        var reply = await StartAsync(app, """{"bpmnProcessId":"waiting","awaitResult":true,"requestTimeoutMs":300}""");

        reply["state"]!.GetValue<string>().ShouldBe("ACTIVE");
        reply["variables"].ShouldBeNull();
    }

    [Fact]
    public async Task FR_API_021_without_the_flag_the_response_has_no_state()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Instant);

        (await StartAsync(app, """{"bpmnProcessId":"instant"}"""))["state"].ShouldBeNull();
    }
}
