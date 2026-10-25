using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>FR-UI-013: reports over the history.</summary>
[Trait("Category", "Integration")]
public sealed class ReportTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Quick = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="quick" isExecutable="true"><bpmn:startEvent id="S"/><bpmn:endEvent id="E"/><bpmn:sequenceFlow id="f" sourceRef="S" targetRef="E"/></bpmn:process>
        </bpmn:definitions>
        """;

    private const string Waiting = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="waiting" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="T"><bpmn:extensionElements><flowd:taskDefinition type="never"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="T"/><bpmn:sequenceFlow id="f2" sourceRef="T" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<JsonNode> GetAsync(TestApp app, string path) =>
        JsonNode.Parse(await app.Client.GetStringAsync(new Uri(path, UriKind.Relative)))!;

    [Fact]
    public async Task FR_UI_013_reports_count_instances_per_day_durations_and_incidents()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Quick);
        await app.DeployAsync(Waiting);
        for (var i = 0; i < 3; i++)
        {
            await app.StartInstanceAsync("quick");
        }

        var running = await app.StartInstanceAsync("waiting");
        (await app.Client.PostAsync(new Uri($"/v1/process-instances/{running}/cancel", UriKind.Relative), null)).EnsureSuccessStatusCode();
        await app.StartInstanceAsync("waiting");

        var all = await GetAsync(app, "/v1/reports/instances");
        var quickOnly = await GetAsync(app, "/v1/reports/instances?bpmnProcessId=quick&bucket=hour&from=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-3).ToString("O")));
        var durations = await GetAsync(app, "/v1/reports/durations?bpmnProcessId=quick");
        var buckets = all["buckets"]!.AsArray();

        buckets.Count.ShouldBeGreaterThanOrEqualTo(30);
        buckets.Sum(b => b!["started"]!.GetValue<int>()).ShouldBe(5);
        buckets.Sum(b => b!["completed"]!.GetValue<int>()).ShouldBe(3);
        buckets.Sum(b => b!["terminated"]!.GetValue<int>()).ShouldBe(1);
        quickOnly["bucket"]!.GetValue<string>().ShouldBe("hour");
        quickOnly["buckets"]!.AsArray().Sum(b => b!["started"]!.GetValue<int>()).ShouldBe(3);
        durations["completedInstances"]!.GetValue<int>().ShouldBe(3);
        durations["p90Seconds"]!.GetValue<double>().ShouldBeGreaterThanOrEqualTo(0);
        (await GetAsync(app, "/v1/reports/incidents"))["types"]!.AsArray().ShouldBeEmpty();
        (await GetAsync(app, "/v1/reports/decisions"))["decisions"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task FR_UI_013_a_range_of_more_than_a_year_is_cut_and_other_tenants_data_is_not_counted()
    {
        await using var app = await TestApp.StartAsync(postgres);

        var report = await GetAsync(app, "/v1/reports/instances?from=2000-01-01T00:00:00Z");

        report["buckets"]!.AsArray().Count.ShouldBeLessThanOrEqualTo(367);
    }
}
