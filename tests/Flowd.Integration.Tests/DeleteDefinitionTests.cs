using System.Net;

namespace Flowd.Integration.Tests;

/// <summary>FR-DEP-006: a process version can be deleted when no instance refers to it.</summary>
[Trait("Category", "Integration")]
public sealed class DeleteDefinitionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static string Process(string id) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="{id}" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="work"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public async Task FR_DEP_006_unused_versions_are_deleted_used_ones_and_unknown_keys_are_refused()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var used = await app.DeployAsync(Process("used"));
        await app.StartInstanceAsync("used");
        var unused = await app.DeployAsync(Process("unused"));

        (await app.Client.DeleteAsync(new Uri($"/v1/process-definitions/{used}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await app.Client.DeleteAsync(new Uri($"/v1/process-definitions/{unused}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.Client.GetAsync(new Uri($"/v1/process-definitions/{unused}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Client.DeleteAsync(new Uri($"/v1/process-definitions/{unused}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Client.DeleteAsync(new Uri("/v1/process-definitions/abc", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
