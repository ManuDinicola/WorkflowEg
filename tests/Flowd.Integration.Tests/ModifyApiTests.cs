using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flowd.Contracts.Execution;

namespace Flowd.Integration.Tests;

/// <summary>FR-OPS-001/002: list running elements, modify an instance and start at a chosen element through the API.</summary>
[Trait("Category", "Integration")]
public sealed class ModifyApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="p" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="a"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:serviceTask id="B"><bpmn:extensionElements><flowd:taskDefinition type="b"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="B"/><bpmn:sequenceFlow id="f3" sourceRef="B" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<JsonArray> ElementsAsync(TestApp app, string key) =>
        (await app.Client.GetFromJsonAsync<JsonObject>(new Uri($"/v1/process-instances/{key}/elements", UriKind.Relative)))!["items"]!.AsArray();

    [Fact]
    public async Task FR_OPS_001_an_operator_moves_the_token_from_one_task_to_another()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        var key = await app.StartInstanceAsync("p");
        var elements = await ElementsAsync(app, key);
        elements.Single()!["elementId"]!.GetValue<string>().ShouldBe("A");

        var response = await app.Client.PostAsJsonAsync(
            new Uri($"/v1/process-instances/{key}/modify", UriKind.Relative),
            new ModifyInstanceRequestDto([elements[0]!["key"]!.GetValue<string>()], ["B"], null));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ElementsAsync(app, key)).Single()!["elementId"]!.GetValue<string>().ShouldBe("B");
        await app.CompleteAsync((await app.ActivateAsync("b")).Single().Key);
        (await app.GetInstanceAsync(key)).State.ShouldBe("COMPLETED");
    }

    [Fact]
    public async Task FR_OPS_002_an_instance_starts_at_the_requested_element_and_bad_requests_are_rejected()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);

        var started = await app.Client.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new StartInstanceRequestDto("p", null, null, null, "B"));
        var key = (await started.Content.ReadFromJsonAsync<StartInstanceResponseDto>())!.ProcessInstanceKey;

        (await ElementsAsync(app, key)).Single()!["elementId"]!.GetValue<string>().ShouldBe("B");
        var bad = await app.Client.PostAsJsonAsync(new Uri($"/v1/process-instances/{key}/modify", UriKind.Relative), new ModifyInstanceRequestDto(["abc"], null, null));
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await app.Client.GetAsync(new Uri("/v1/process-instances/999/elements", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
