using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>FR-API-011/012: search jobs, filter instances by variable, sort newest first.</summary>
[Trait("Category", "Integration")]
public sealed class SearchApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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

    private static async Task<JsonArray> ItemsAsync(TestApp app, string url) =>
        (await app.Client.GetFromJsonAsync<JsonObject>(new Uri(url, UriKind.Relative)))!["items"]!.AsArray();

    [Fact]
    public async Task FR_API_012_instances_filter_by_variable_and_sort_newest_first_with_paging()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        var first = await app.StartInstanceAsync("p", """{"customer":"acme","size":3}""");
        var second = await app.StartInstanceAsync("p", """{"customer":"globex","size":3}""");
        var third = await app.StartInstanceAsync("p", """{"customer":"acme","size":5}""");

        (await ItemsAsync(app, "/v1/process-instances?variable=customer:acme")).Select(i => i!["key"]!.GetValue<string>()).ShouldBe([first, third]);
        (await ItemsAsync(app, "/v1/process-instances?variable=size:3")).Count.ShouldBe(2);
        (await ItemsAsync(app, "/v1/process-instances?variable=customer:nobody")).ShouldBeEmpty();
        var newestFirst = await app.Client.GetFromJsonAsync<JsonObject>(new Uri("/v1/process-instances?order=desc&pageSize=2", UriKind.Relative));
        newestFirst!["items"]!.AsArray().Select(i => i!["key"]!.GetValue<string>()).ShouldBe([third, second]);
        var next = await ItemsAsync(app, $"/v1/process-instances?order=desc&pageSize=2&after={newestFirst["nextCursor"]!.GetValue<string>()}");
        next.Single()!["key"]!.GetValue<string>().ShouldBe(first);
    }

    [Fact]
    public async Task FR_API_011_jobs_are_searchable_by_type_state_instance_and_worker()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        var one = await app.StartInstanceAsync("p");
        await app.StartInstanceAsync("p");
        var taken = (await app.ActivateAsync("work", worker: "w-1", maxJobs: 1)).Single();

        (await ItemsAsync(app, "/v1/jobs?type=work")).Count.ShouldBe(2);
        (await ItemsAsync(app, "/v1/jobs?state=activatable")).Count.ShouldBe(1);
        var activated = (await ItemsAsync(app, "/v1/jobs?state=ACTIVATED&worker=w-1")).Single()!;
        activated["key"]!.GetValue<string>().ShouldBe(taken.Key);
        (await ItemsAsync(app, $"/v1/jobs?processInstanceKey={one}")).Count.ShouldBe(1);
        (await ItemsAsync(app, "/v1/jobs?type=other")).ShouldBeEmpty();
    }
}
