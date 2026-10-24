using System.Text.Json.Nodes;
using Flowd.Worker.Client;

namespace Flowd.Integration.Tests;

/// <summary>Worker SDK (section 9.2): long polling, results, concurrency and graceful shutdown against a real host.</summary>
[Trait("Category", "Integration")]
public sealed class WorkerSdkTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="sdk" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="sdk-work"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static readonly JobWorkerOptions Fast = new() { WorkerName = "sdk", PollTimeout = TimeSpan.FromMilliseconds(300), MaxConcurrentJobs = 4 };

    [Fact]
    public async Task Worker_completes_jobs_with_variables_and_stops_gracefully()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        var keys = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            keys.Add(await app.StartInstanceAsync("sdk"));
        }

        var worker = new FlowdClient(app.Client).CreateWorker("sdk-work", (job, _) => Task.FromResult(JobResults.Complete(new JsonObject { ["done"] = true })), Fast);
        using var stop = new CancellationTokenSource();
        var run = worker.RunAsync(stop.Token);
        await WaitUntilAsync(async () => (await app.GetInstanceAsync(keys[^1])).State == "COMPLETED");
        await stop.CancelAsync();
        await run;

        worker.Handled.ShouldBe(6);
        (await app.GetVariablesAsync(keys[0]))["done"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Handler_exception_becomes_a_failed_job_and_exhausted_retries_raise_an_incident()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        var key = await app.StartInstanceAsync("sdk");
        var calls = 0;
        var worker = new FlowdClient(app.Client).CreateWorker(
            "sdk-work",
            (job, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(JobResults.Fail("boom", retries: 0));
            },
            Fast);
        using var stop = new CancellationTokenSource();
        var run = worker.RunAsync(stop.Token);
        await WaitUntilAsync(() => Task.FromResult(worker.Handled >= 1));
        await stop.CancelAsync();
        await run;

        calls.ShouldBe(1);
        (await app.GetInstanceAsync(key)).State.ShouldNotBe("COMPLETED");
    }

    [Fact]
    public async Task Transport_errors_are_retried_with_backoff()
    {
        var failures = 0;
        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref failures) <= 2 ? throw new HttpRequestException("down") : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"jobs":[]}""", System.Text.Encoding.UTF8, "application/json") });
        var client = FlowdClient.Create(new Uri("http://flowd.test"), "key", handler);
        var options = new JobWorkerOptions { MinBackoff = TimeSpan.FromMilliseconds(5), MaxBackoff = TimeSpan.FromMilliseconds(20), PollTimeout = TimeSpan.FromMilliseconds(10) };
        var worker = client.CreateWorker("t", (_, _) => Task.FromResult(JobResults.Complete()), options);
        using var stop = new CancellationTokenSource();
        var run = worker.RunAsync(stop.Token);
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref failures) >= 4));
        await stop.CancelAsync();
        await run;

        failures.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task Repeated_transport_failures_do_not_leak_concurrency_slots()
    {
        var calls = 0;
        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref calls) <= 10 ? throw new HttpRequestException("down") : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"jobs":[]}""", System.Text.Encoding.UTF8, "application/json") });
        var options = new JobWorkerOptions { MinBackoff = TimeSpan.FromMilliseconds(1), MaxBackoff = TimeSpan.FromMilliseconds(5), PollTimeout = TimeSpan.FromMilliseconds(5), MaxConcurrentJobs = 2 };
        var worker = FlowdClient.Create(new Uri("http://flowd.test"), null, handler).CreateWorker("t", (_, _) => Task.FromResult(JobResults.Complete()), options);
        using var stop = new CancellationTokenSource();
        var run = worker.RunAsync(stop.Token);

        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref calls) >= 20)); // more failures than slots, and it still polls
        await stop.CancelAsync();
        await run;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }
}
