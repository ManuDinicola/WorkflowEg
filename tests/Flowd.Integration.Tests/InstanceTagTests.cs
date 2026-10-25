using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>FR-EXEC-020: tags given at start are stored, shown and searchable.</summary>
[Trait("Category", "Integration")]
public sealed class InstanceTagTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="t" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="work"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<HttpResponseMessage> StartAsync(TestApp app, params string[] tags) =>
        await app.Client.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new { bpmnProcessId = "t", tags });

    [Fact]
    public async Task FR_EXEC_020_tags_are_stored_returned_and_can_be_used_to_search()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        (await StartAsync(app, "team:billing", "priority-high")).EnsureSuccessStatusCode();
        (await StartAsync(app, "team:sales")).EnsureSuccessStatusCode();
        (await StartAsync(app)).EnsureSuccessStatusCode();

        var billing = JsonNode.Parse(await app.Client.GetStringAsync(new Uri("/v1/process-instances?tag=team:billing", UriKind.Relative)))!["items"]!.AsArray();
        var teamPrefixOnly = JsonNode.Parse(await app.Client.GetStringAsync(new Uri("/v1/process-instances?tag=team", UriKind.Relative)))!["items"]!.AsArray();
        var all = JsonNode.Parse(await app.Client.GetStringAsync(new Uri("/v1/process-instances", UriKind.Relative)))!["items"]!.AsArray();

        billing.Count.ShouldBe(1);
        billing[0]!["tags"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe(["team:billing", "priority-high"]);
        teamPrefixOnly.Count.ShouldBe(0); // a tag matches exactly, not by prefix
        all.Count.ShouldBe(3);
    }

    [Fact]
    public async Task FR_EXEC_020_too_many_or_badly_formed_tags_are_refused()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);

        (await StartAsync(app, "has space")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StartAsync(app, "a,b")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StartAsync(app, "1starts-with-digit")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StartAsync(app, [.. Enumerable.Range(0, 11).Select(i => "t" + i)])).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonNode.Parse(await app.Client.GetStringAsync(new Uri("/v1/process-instances", UriKind.Relative)))!["items"]!.AsArray().ShouldBeEmpty();
    }
}
