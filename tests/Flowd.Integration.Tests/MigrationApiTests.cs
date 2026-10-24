using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flowd.Contracts.Execution;

namespace Flowd.Integration.Tests;

/// <summary>FR-OPS-004: migrating running instances to another version of their process.</summary>
[Trait("Category", "Integration")]
public sealed class MigrationApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string V1 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="m" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="a"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private const string V2 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="m" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A2"><bpmn:extensionElements><flowd:taskDefinition type="a"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:serviceTask id="B"><bpmn:extensionElements><flowd:taskDefinition type="b"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A2"/><bpmn:sequenceFlow id="f2" sourceRef="A2" targetRef="B"/><bpmn:sequenceFlow id="f3" sourceRef="B" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<HttpResponseMessage> MigrateAsync(TestApp app, string instance, string target, params (string From, string To)[] mapping) =>
        await app.Client.PostAsJsonAsync(
            new Uri($"/v1/process-instances/{instance}/migrate", UriKind.Relative),
            new MigrateInstanceRequestDto(target, mapping.ToDictionary(m => m.From, m => m.To)));

    [Fact]
    public async Task FR_OPS_004_a_waiting_instance_continues_on_the_new_version_after_the_mapping_is_applied()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(V1);
        var instance = await app.StartInstanceAsync("m");
        var v2 = await app.DeployAsync(V2);

        (await MigrateAsync(app, instance, v2, ("A", "A2"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var elements = (await app.Client.GetFromJsonAsync<JsonObject>(new Uri($"/v1/process-instances/{instance}/elements", UriKind.Relative)))!["items"]!.AsArray();
        elements.Single()!["elementId"]!.GetValue<string>().ShouldBe("A2");
        await app.CompleteAsync((await app.ActivateAsync("a")).Single().Key);
        elements = (await app.Client.GetFromJsonAsync<JsonObject>(new Uri($"/v1/process-instances/{instance}/elements", UriKind.Relative)))!["items"]!.AsArray();
        elements.Single()!["elementId"]!.GetValue<string>().ShouldBe("B"); // only the new version has step B
        await app.CompleteAsync((await app.ActivateAsync("b")).Single().Key);
        (await app.GetInstanceAsync(instance)).State.ShouldBe("COMPLETED");
        (await app.GetInstanceAsync(instance)).Version.ShouldBe(2);
    }

    [Fact]
    public async Task FR_OPS_004_a_missing_mapping_other_processes_and_repeated_migrations_are_refused()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var v1 = await app.DeployAsync(V1);
        var instance = await app.StartInstanceAsync("m");
        var v2 = await app.DeployAsync(V2);

        var unmapped = await MigrateAsync(app, instance, v2);
        unmapped.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unmapped.Content.ReadAsStringAsync()).ShouldContain("TARGET_ELEMENT_MISSING");
        (await MigrateAsync(app, instance, v1, ("A", "A"))).StatusCode.ShouldBe(HttpStatusCode.Conflict); // already on v1
        (await MigrateAsync(app, "999", v2)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.GetInstanceAsync(instance)).Version.ShouldBe(1); // nothing changed
    }

    [Fact]
    public async Task FR_OPS_004_the_batch_migrates_all_active_instances_of_a_process()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(V1);
        var first = await app.StartInstanceAsync("m");
        var second = await app.StartInstanceAsync("m");
        var v2 = await app.DeployAsync(V2);

        var response = await app.Client.PostAsJsonAsync(
            new Uri("/v1/batch/migrate-instances", UriKind.Relative),
            new { bpmnProcessId = "m", targetDefinitionKey = v2, mapping = new Dictionary<string, string> { ["A"] = "A2" } });
        var result = (await response.Content.ReadFromJsonAsync<JsonObject>())!;

        result["matched"]!.GetValue<int>().ShouldBe(2);
        result["succeeded"]!.GetValue<int>().ShouldBe(2);
        (await app.GetInstanceAsync(first)).Version.ShouldBe(2);
        (await app.GetInstanceAsync(second)).Version.ShouldBe(2);
    }
}
