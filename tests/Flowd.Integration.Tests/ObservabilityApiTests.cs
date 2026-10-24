using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Flowd.Contracts.Execution;

namespace Flowd.Integration.Tests;

/// <summary>FR-OBS-001..003: metrics, traces and structured logs.</summary>
[Trait("Category", "Integration")]
[Collection(ObservabilityCollection.Name)]
public sealed class ObservabilityApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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
    public async Task FR_OBS_001_the_metrics_endpoint_exposes_instance_job_and_command_metrics()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        await app.StartInstanceAsync("p");
        var job = (await app.ActivateAsync("work")).Single();
        await app.CompleteAsync(job.Key);

        var response = await app.Client.GetAsync(new Uri("/metrics", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        text.ShouldContain("flowd_process_instances_started_total 1");
        text.ShouldContain("flowd_process_instances_completed_total 1");
        text.ShouldContain("flowd_process_instances_active 0");
        text.ShouldContain("flowd_jobs_created_total{type=\"work\"} 1");
        text.ShouldContain("flowd_jobs_activated_total{type=\"work\"} 1");
        text.ShouldContain("flowd_command_duration_seconds_count{command=\"CompleteJob\"} 1");
        text.ShouldContain("flowd_command_lock_wait_seconds_sum");
        text.ShouldContain("flowd_job_activation_latency_seconds_count{type=\"work\"} 1");
    }

    [Fact]
    public async Task FR_OBS_002_every_command_runs_in_a_span_that_carries_the_instance_key()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Flowd",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        var instance = await app.StartInstanceAsync("p");
        var job = (await app.ActivateAsync("work")).Single();

        await app.CompleteAsync(job.Key);

        var span = spans.Single(s => s.OperationName == "command CompleteJob");
        span.GetTagItem("flowd.instanceKey").ShouldBe(long.Parse(instance, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task FR_OBS_002_an_incoming_traceparent_header_is_continued()
    {
        var traceIds = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Flowd" || source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => traceIds.Enqueue(activity.TraceId.ToString()),
        };
        ActivitySource.AddActivityListener(listener);
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/process-instances", UriKind.Relative))
        {
            Content = JsonContent.Create(new StartInstanceRequestDto("p", null, null, null, null)),
        };
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        (await app.Client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.OK);

        for (var i = 0; i < 50 && !traceIds.Contains("0af7651916cd43dd8448eb211c80319c"); i++)
        {
            await Task.Delay(100); // the server finishes its span just after the response is sent
        }

        traceIds.ShouldContain("0af7651916cd43dd8448eb211c80319c");
    }
}

/// <summary>Meters and activity sources are process-wide, so these tests must not overlap with other hosts running in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class ObservabilityCollection
{
    public const string Name = "Observability";
}
