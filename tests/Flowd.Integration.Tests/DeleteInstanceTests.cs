using System.Net;

namespace Flowd.Integration.Tests;

/// <summary>FR-DB-003: finished instances can be deleted on demand, running ones cannot.</summary>
[Trait("Category", "Integration")]
public sealed class DeleteInstanceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="p" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="work"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public async Task FR_DB_003_a_finished_instance_is_deleted_and_a_running_one_is_refused()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        var key = await app.StartInstanceAsync("p");

        (await app.Client.DeleteAsync(new Uri($"/v1/process-instances/{key}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await app.CompleteAsync((await app.ActivateAsync("work")).Single().Key);
        (await app.Client.DeleteAsync(new Uri($"/v1/process-instances/{key}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.Client.GetAsync(new Uri($"/v1/process-instances/{key}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Client.DeleteAsync(new Uri($"/v1/process-instances/{key}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
