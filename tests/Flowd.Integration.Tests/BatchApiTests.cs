using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flowd.Contracts.Execution;

namespace Flowd.Integration.Tests;

/// <summary>FR-OPS-003: batch cancel and batch incident resolution.</summary>
[Trait("Category", "Integration")]
public sealed class BatchApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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

    private static async Task<JsonObject> PostAsync(TestApp app, string url, string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await app.Client.PostAsync(new Uri(url, UriKind.Relative), content);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task FR_OPS_003_batch_cancel_respects_the_cap_and_can_be_repeated_for_the_rest()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        var keys = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            keys.Add(await app.StartInstanceAsync("p"));
        }

        var first = await PostAsync(app, "/v1/batch/cancel-instances", """{"bpmnProcessId":"p","maxItems":3}""");
        var second = await PostAsync(app, "/v1/batch/cancel-instances", """{"bpmnProcessId":"p"}""");

        first["matched"]!.GetValue<int>().ShouldBe(3);
        first["succeeded"]!.GetValue<int>().ShouldBe(3);
        first["truncated"]!.GetValue<bool>().ShouldBeTrue();
        second["matched"]!.GetValue<int>().ShouldBe(2);
        second["truncated"]!.GetValue<bool>().ShouldBeFalse();
        foreach (var key in keys)
        {
            (await app.GetInstanceAsync(key)).State.ShouldBe("TERMINATED");
        }
    }

    [Fact]
    public async Task FR_OPS_003_batch_resolve_retries_the_jobs_of_all_open_incidents()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        await app.StartInstanceAsync("p");
        await app.StartInstanceAsync("p");
        foreach (var job in await app.ActivateAsync("work"))
        {
            (await app.Client.PostAsJsonAsync(new Uri($"/v1/jobs/{job.Key}/fail", UriKind.Relative), new FailJobRequestDto(0, "boom", null, "w1"))).EnsureSuccessStatusCode();
            (await app.Client.PatchAsJsonAsync(new Uri($"/v1/jobs/{job.Key}", UriKind.Relative), new UpdateJobRequestDto(1, null))).EnsureSuccessStatusCode(); // an incident can only be resolved once the job has retries again
        }

        var result = await PostAsync(app, "/v1/batch/resolve-incidents", "{}");

        result["matched"]!.GetValue<int>().ShouldBe(2);
        result["succeeded"]!.GetValue<int>().ShouldBe(2);
        (await app.ActivateAsync("work")).Count.ShouldBe(2);
        (await PostAsync(app, "/v1/batch/resolve-incidents", "{}"))["matched"]!.GetValue<int>().ShouldBe(0);
    }
}
