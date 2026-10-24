using System.Net.Http.Json;
using Flowd.Contracts.Execution;

namespace Flowd.Integration.Tests;

/// <summary>FR-JOB-008: a worker can ask for only the variables it needs.</summary>
[Trait("Category", "Integration")]
public sealed class FetchVariablesTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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
    public async Task FR_JOB_008_only_the_requested_variables_are_returned()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        await app.StartInstanceAsync("p", """{"keep":1,"drop":2,"also":3}""");

        var response = await app.Client.PostAsJsonAsync(new Uri("/v1/jobs/activate", UriKind.Relative), new ActivateJobsRequestDto("work", "w", 30000, 1, 0, ["keep", "also"]));
        var job = (await response.Content.ReadFromJsonAsync<ActivateJobsResponseDto>())!.Jobs.Single();

        job.Variables.ContainsKey("keep").ShouldBeTrue();
        job.Variables.ContainsKey("also").ShouldBeTrue();
        job.Variables.ContainsKey("drop").ShouldBeFalse();
    }
}
