using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flowd.Application.Jobs;
using Flowd.Contracts.Deployments;
using Flowd.Contracts.Execution;
using Flowd.Infrastructure.Postgres.Execution;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>M2 end to end on a real PostgreSQL: instances, jobs, incidents, concurrency (T-EXEC, T-JOB, FR-CON).</summary>
[Trait("Category", "Integration")]
public sealed class ExecutionApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static string OrderXml() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "order-simple.bpmn"));

    private static string Task(string id, string type) =>
        $"<bpmn:serviceTask id=\"{id}\"><bpmn:extensionElements><flowd:taskDefinition type=\"{type}\"/></bpmn:extensionElements></bpmn:serviceTask>";

    private static string Process(string id, string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="{id}" isExecutable="true">{body}</bpmn:process>
        </bpmn:definitions>
        """;

    private static string Linear(string id, string type) => Process(
        id,
        $"<bpmn:startEvent id=\"S\"/>{Task("A", type)}<bpmn:endEvent id=\"E\"/>" +
        "<bpmn:sequenceFlow id=\"f1\" sourceRef=\"S\" targetRef=\"A\"/><bpmn:sequenceFlow id=\"f2\" sourceRef=\"A\" targetRef=\"E\"/>");

    [Fact]
    public async Task Order_process_runs_end_to_end_for_a_small_and_a_big_order()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(OrderXml());

        var small = await app.StartInstanceAsync("order-process", "{\"order\":{\"total\":50}}");
        var charge = (await app.ActivateAsync("charge-card")).ShouldHaveSingleItem();
        charge.Variables["amount"]!.GetValue<decimal>().ShouldBe(50);
        (await app.CompleteAsync(charge.Key, "{\"receiptId\":\"R-1\"}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await app.GetInstanceAsync(small)).State.ShouldBe("COMPLETED");
        (await app.GetVariablesAsync(small))["receiptId"]!.GetValue<string>().ShouldBe("R-1");
        (await app.ActivateAsync("review-order")).ShouldBeEmpty();

        var big = await app.StartInstanceAsync("order-process", "{\"order\":{\"total\":5000}}");
        var charge2 = (await app.ActivateAsync("charge-card")).ShouldHaveSingleItem();
        await app.CompleteAsync(charge2.Key, "{\"receiptId\":\"R-2\"}");
        var review = (await app.ActivateAsync("review-order")).ShouldHaveSingleItem();
        review.ProcessInstanceKey.ShouldBe(big);
        (await app.GetInstanceAsync(big)).State.ShouldBe("ACTIVE");
        await app.CompleteAsync(review.Key);
        (await app.GetInstanceAsync(big)).State.ShouldBe("COMPLETED");
    }

    [Fact]
    public async Task History_endpoint_returns_ordered_events()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Linear("p", "work"));
        var key = await app.StartInstanceAsync("p");

        var page = await app.Client.GetFromJsonAsync<PageDto<HistoryEventDto>>(new Uri($"/v1/process-instances/{key}/history", UriKind.Relative));

        page!.Items.First().EventType.ShouldBe("INSTANCE_STARTED");
        page.Items.Select(i => long.Parse(i.Seq, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(page.Items.Select(i => long.Parse(i.Seq, System.Globalization.CultureInfo.InvariantCulture)).Order());
        page.Items.Select(i => i.EventType).ShouldContain("JOB_CREATED");
    }

    [Fact]
    public async Task T_EXEC_09_cancel_terminates_the_instance_and_its_job_can_no_longer_be_completed()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Linear("p", "work"));
        var key = await app.StartInstanceAsync("p");
        var job = (await app.ActivateAsync("work")).Single();

        (await app.Client.PostAsync(new Uri($"/v1/process-instances/{key}/cancel", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await app.GetInstanceAsync(key)).State.ShouldBe("TERMINATED");
        var late = await app.CompleteAsync(job.Key);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await TestApp.ProblemCodeAsync(late)).ShouldBe("JOB_NOT_ACTIVATED");
        (await app.Client.PostAsync(new Uri($"/v1/process-instances/{key}/cancel", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task T_JOB_05_completing_twice_returns_409_job_not_activated()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Linear("p", "work"));
        await app.StartInstanceAsync("p");
        var job = (await app.ActivateAsync("work")).Single();
        (await app.CompleteAsync(job.Key)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var second = await app.CompleteAsync(job.Key);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await TestApp.ProblemCodeAsync(second)).ShouldBe("JOB_NOT_ACTIVATED");
    }

    [Fact]
    public async Task T_JOB_01_twenty_concurrent_activators_never_receive_the_same_job()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Linear("p", "work"));
        for (var i = 0; i < 30; i++)
        {
            await app.StartInstanceAsync("p");
        }

        var batches = await System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, 20).Select(i => app.ActivateAsync("work", $"w{i}", maxJobs: 3)));

        var keys = batches.SelectMany(b => b).Select(j => j.Key).ToList();
        keys.Distinct().Count().ShouldBe(keys.Count);
        keys.Count.ShouldBe(30);
        batches.ShouldAllBe(b => b.Count <= 3);
    }

    [Fact]
    public async Task T_JOB_08_long_poll_waits_for_a_job_and_returns_empty_after_the_request_timeout()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Linear("p", "work"));

        var started = System.Diagnostics.Stopwatch.StartNew();
        (await app.ActivateAsync("work", requestTimeoutMs: 400)).ShouldBeEmpty();
        started.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(350));

        var poll = app.ActivateAsync("work", requestTimeoutMs: 5000);
        await System.Threading.Tasks.Task.Delay(300);
        await app.StartInstanceAsync("p");

        (await poll).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task FR_CON_001_completing_both_parallel_branches_concurrently_continues_exactly_once()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process(
            "p",
            "<bpmn:startEvent id=\"S\"/><bpmn:parallelGateway id=\"Fork\"/>" + Task("A", "a") + Task("B", "b") +
            "<bpmn:parallelGateway id=\"Join\"/>" + Task("C", "c") + "<bpmn:endEvent id=\"E\"/>" +
            "<bpmn:sequenceFlow id=\"f0\" sourceRef=\"S\" targetRef=\"Fork\"/><bpmn:sequenceFlow id=\"f1\" sourceRef=\"Fork\" targetRef=\"A\"/>" +
            "<bpmn:sequenceFlow id=\"f2\" sourceRef=\"Fork\" targetRef=\"B\"/><bpmn:sequenceFlow id=\"f3\" sourceRef=\"A\" targetRef=\"Join\"/>" +
            "<bpmn:sequenceFlow id=\"f4\" sourceRef=\"B\" targetRef=\"Join\"/><bpmn:sequenceFlow id=\"f5\" sourceRef=\"Join\" targetRef=\"C\"/>" +
            "<bpmn:sequenceFlow id=\"f6\" sourceRef=\"C\" targetRef=\"E\"/>"));
        await app.StartInstanceAsync("p");
        var a = (await app.ActivateAsync("a")).Single();
        var b = (await app.ActivateAsync("b")).Single();

        var responses = await System.Threading.Tasks.Task.WhenAll(app.CompleteAsync(a.Key), app.CompleteAsync(b.Key));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent);
        (await app.ActivateAsync("c")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task T_JOB_03_and_04_failing_without_retries_creates_an_incident_that_can_be_resolved_after_updating_retries()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Linear("p", "work"));
        var key = await app.StartInstanceAsync("p");
        var job = (await app.ActivateAsync("work")).Single();

        var fail = await app.Client.PostAsJsonAsync(new Uri($"/v1/jobs/{job.Key}/fail", UriKind.Relative), new FailJobRequestDto(0, "no good", null, null));
        fail.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var incidents = await app.Client.GetFromJsonAsync<PageDto<IncidentDto>>(new Uri($"/v1/incidents?processInstanceKey={key}&state=OPEN", UriKind.Relative));
        var incident = incidents!.Items.ShouldHaveSingleItem();
        incident.ErrorType.ShouldBe("JOB_NO_RETRIES");

        (await app.Client.PostAsync(new Uri($"/v1/incidents/{incident.Key}/resolve", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var patch = await app.Client.PatchAsJsonAsync(new Uri($"/v1/jobs/{job.Key}", UriKind.Relative), new UpdateJobRequestDto(3, null));
        patch.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.Client.PostAsync(new Uri($"/v1/incidents/{incident.Key}/resolve", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var again = (await app.ActivateAsync("work")).ShouldHaveSingleItem();
        again.Retries.ShouldBe(3);
    }

    [Fact]
    public async Task T_JOB_02_a_job_past_its_deadline_is_requeued_with_unchanged_retries()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Linear("p", "work"));
        await app.StartInstanceAsync("p");
        await using var dataSource = NpgsqlDataSource.Create(app.ConnectionString);
        var queue = new PostgresJobQueue(dataSource);
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var first = await queue.ActivateAsync(new ActivateJobsRequest(string.Empty, "work", "w1", TimeSpan.FromSeconds(30), 1, null), now, CancellationToken.None);
        (await queue.ActivateAsync(new ActivateJobsRequest(string.Empty, "work", "w2", TimeSpan.FromSeconds(30), 1, null), now, CancellationToken.None)).ShouldBeEmpty();
        (await queue.RequeueTimedOutAsync(now + TimeSpan.FromSeconds(10), CancellationToken.None)).ShouldBe(0);
        (await queue.RequeueTimedOutAsync(now + TimeSpan.FromSeconds(31), CancellationToken.None)).ShouldBe(1);

        var second = await queue.ActivateAsync(new ActivateJobsRequest(string.Empty, "work", "w2", TimeSpan.FromSeconds(30), 1, null), now + TimeSpan.FromSeconds(31), CancellationToken.None);
        second.ShouldHaveSingleItem().Key.ShouldBe(first.Single().Key);
        second.Single().Retries.ShouldBe(3);
    }

    [Fact]
    public async Task FR_JOB_006_set_variables_and_unknown_resources_return_proper_errors()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Linear("p", "work"));
        var key = await app.StartInstanceAsync("p", "{\"a\":1}");

        var ok = await app.Client.PutAsJsonAsync(new Uri($"/v1/process-instances/{key}/variables", UriKind.Relative), new SetVariablesRequestDto(JsonNode.Parse("{\"a\":2,\"b\":3}")!.AsObject(), null, false));
        var reserved = await app.Client.PutAsJsonAsync(new Uri($"/v1/process-instances/{key}/variables", UriKind.Relative), new SetVariablesRequestDto(JsonNode.Parse("{\"_flowdX\":1}")!.AsObject(), null, false));

        ok.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        reserved.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var variables = await app.GetVariablesAsync(key);
        variables["a"]!.GetValue<int>().ShouldBe(2);
        variables["b"]!.GetValue<int>().ShouldBe(3);
        (await app.Client.GetAsync(new Uri("/v1/process-instances/999", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.CompleteAsync("12345")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var start = await app.Client.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new StartInstanceRequestDto("nope", null, null, null, null));
        start.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
