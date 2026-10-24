using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using Flowd.Engine.State;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>T-JOB-02..07 on the in-memory engine; concurrency and long-poll timing run against PostgreSQL (integration tests).</summary>
public sealed class JobTests
{
    private static async Task<(TestEngine Engine, long Instance)> StartSingleTaskAsync(string? retries = null)
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), Service("A", "work", retries), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        return (engine, await engine.StartAsync("p", JsonNode.Parse("{\"a\":1,\"b\":2}")!.AsObject()));
    }

    [Fact]
    public async Task T_JOB_02_a_job_not_completed_before_its_deadline_becomes_activatable_again_with_unchanged_retries()
    {
        var (engine, _) = await StartSingleTaskAsync();
        var first = (await engine.ActivateJobAsync("work", timeout: TimeSpan.FromSeconds(30))).ShouldNotBeNull();
        (await engine.ActivateJobAsync("work")).ShouldBeNull();

        engine.AdvanceTime(TimeSpan.FromSeconds(31));
        (await engine.RequeueTimedOutAsync()).ShouldBe(1);

        var second = (await engine.ActivateJobAsync("work", worker: "worker-2")).ShouldNotBeNull();
        second.Key.ShouldBe(first.Key);
        second.Retries.ShouldBe(3);
    }

    [Fact]
    public async Task T_JOB_03_failing_with_retries_left_requeues_after_backoff_and_zero_retries_raises_an_incident()
    {
        var (engine, instance) = await StartSingleTaskAsync();
        var job = (await engine.ActivateJobAsync("work"))!;

        TestEngine.Ok(await engine.FailJobAsync(job.Key, retries: 2, backoff: TimeSpan.FromSeconds(10)));
        (await engine.ActivateJobAsync("work")).ShouldBeNull();
        engine.AdvanceTime(TimeSpan.FromSeconds(11));
        var again = (await engine.ActivateJobAsync("work")).ShouldNotBeNull();
        again.Retries.ShouldBe(2);

        TestEngine.Ok(await engine.FailJobAsync(again.Key, retries: 0, message: "gave up"));

        var incident = engine.OpenIncidents(instance).ShouldHaveSingleItem();
        incident.ErrorType.ShouldBe("JOB_NO_RETRIES");
        incident.JobKey.ShouldBe(again.Key);
        engine.State(instance).Jobs[again.Key].State.ShouldBe(JobState.Failed);
    }

    [Fact]
    public async Task T_JOB_04_updating_retries_and_resolving_the_incident_makes_the_job_activatable_again()
    {
        var (engine, instance) = await StartSingleTaskAsync();
        var job = (await engine.ActivateJobAsync("work"))!;
        TestEngine.Ok(await engine.FailJobAsync(job.Key, retries: 0));
        var incident = engine.OpenIncidents(instance).Single();

        (TestEngine.ConflictCode(await engine.ResolveIncidentAsync(incident.Key)) is null).ShouldBeTrue();
        engine.OpenIncidents(instance).ShouldHaveSingleItem();

        TestEngine.Ok(await engine.UpdateRetriesAsync(job.Key, 3));
        TestEngine.Ok(await engine.ResolveIncidentAsync(incident.Key));

        engine.OpenIncidents(instance).ShouldBeEmpty();
        engine.State(instance).Incidents[incident.Key].State.ShouldBe(IncidentState.Resolved);
        var retried = (await engine.ActivateJobAsync("work")).ShouldNotBeNull();
        retried.Retries.ShouldBe(3);
    }

    [Fact]
    public async Task T_JOB_05_completing_a_job_twice_conflicts_the_second_time()
    {
        var (engine, _) = await StartSingleTaskAsync();
        var job = (await engine.ActivateJobAsync("work"))!;
        TestEngine.Ok(await engine.CompleteJobAsync(job.Key));

        TestEngine.ConflictCode(await engine.CompleteJobAsync(job.Key)).ShouldBe("JOB_NOT_ACTIVATED");
    }

    [Fact]
    public async Task T_JOB_06_a_stale_worker_cannot_complete_a_job_that_another_worker_now_holds()
    {
        var (engine, _) = await StartSingleTaskAsync();
        var job = (await engine.ActivateJobAsync("work", worker: "worker-1", timeout: TimeSpan.FromSeconds(30)))!;
        engine.AdvanceTime(TimeSpan.FromSeconds(31));
        await engine.RequeueTimedOutAsync();
        (await engine.ActivateJobAsync("work", worker: "worker-2")).ShouldNotBeNull();

        TestEngine.ConflictCode(await engine.CompleteJobAsync(job.Key, worker: "worker-1")).ShouldBe("JOB_NOT_ACTIVATED");
        TestEngine.Ok(await engine.CompleteJobAsync(job.Key, worker: "worker-2"));
    }

    [Fact]
    public async Task T_JOB_07_fetch_variables_limits_the_returned_variables()
    {
        var (engine, _) = await StartSingleTaskAsync();

        var job = (await engine.ActivateJobAsync("work", fetch: ["a"])).ShouldNotBeNull();

        job.Variables.Select(p => p.Key).ShouldBe(["a"]);
    }

    [Fact]
    public async Task FR_JOB_009_static_task_headers_are_returned_with_the_job()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Service("A", "work", inner: "<flowd:taskHeaders><flowd:header key=\"region\" value=\"eu\"/></flowd:taskHeaders>"),
            End(),
            Flow("f1", "Start", "A"),
            Flow("f2", "A", "End")));
        await engine.StartAsync("p");

        var job = (await engine.ActivateJobAsync("work")).ShouldNotBeNull();

        job.CustomHeaders["region"].ShouldBe("eu");
    }

    [Fact]
    public async Task FR_JOB_005_a_thrown_error_without_a_handler_raises_unhandled_error_event()
    {
        var (engine, instance) = await StartSingleTaskAsync();
        var job = (await engine.ActivateJobAsync("work"))!;

        TestEngine.Ok(await engine.ThrowErrorAsync(job.Key, "PAYMENT_DECLINED"));

        engine.OpenIncidents(instance).ShouldHaveSingleItem().ErrorType.ShouldBe("UNHANDLED_ERROR_EVENT");
        engine.State(instance).Jobs[job.Key].State.ShouldBe(JobState.ErrorThrown);
    }

    [Fact]
    public async Task FR_EXEC_006_retries_can_be_an_expression()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), Service("A", "work", retries: "= attempts + 1"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        await engine.StartAsync("p", JsonNode.Parse("{\"attempts\":4}")!.AsObject());

        (await engine.ActivateJobAsync("work"))!.Retries.ShouldBe(5);
    }
}
