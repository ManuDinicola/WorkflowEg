using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>FR-LST-001: non-blocking execution listeners as jobs.</summary>
public sealed class ListenerTests
{
    private const string Listeners = "<flowd:executionListeners><flowd:listener event=\"start\" type=\"on-start\"/><flowd:listener event=\"end\" type=\"on-end\"/></flowd:executionListeners>";

    private static string ListenerProcess() => Process(
        "p",
        Start(),
        Service("Work", "work", inner: Listeners),
        End(),
        Flow("f1", "Start", "Work"),
        Flow("f2", "Work", "End"));

    [Fact]
    public async Task FR_LST_001_start_and_end_listeners_create_jobs_without_blocking_the_flow()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ListenerProcess());
        var key = await engine.StartAsync("p");

        var start = await engine.ActivateJobAsync("on-start");
        start.ShouldNotBeNull();
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("work"))!.Key));
        TestEngine.Ok(await engine.CompleteJobAsync(start.Key));
        engine.AssertCompleted(key);
        (await engine.ActivateJobAsync("on-end")).ShouldNotBeNull();
    }

    [Fact]
    public async Task FR_LST_001_a_failing_listener_never_raises_an_incident()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ListenerProcess());
        var key = await engine.StartAsync("p");

        TestEngine.Ok(await engine.FailJobAsync((await engine.ActivateJobAsync("on-start"))!.Key, 0));

        engine.AssertActiveElements(key, "Work");
    }

    [Fact]
    public async Task FR_LST_001_task_listeners_fire_on_created_and_assigned()
    {
        var engine = new TestEngine();
        const string inner = "<flowd:taskListeners><flowd:listener event=\"created\" type=\"t-created\"/><flowd:listener event=\"assigned\" type=\"t-assigned\"/></flowd:taskListeners>";
        await engine.DeployAsync(Process("p", Start(), UserTask("Review", inner: inner), End(), Flow("f1", "Start", "Review"), Flow("f2", "Review", "End")));
        var key = await engine.StartAsync("p");

        (await engine.ActivateJobAsync("t-created")).ShouldNotBeNull();
        TestEngine.Ok(await engine.ClaimTaskAsync(engine.UserTasks(key).Single().Key, "ann"));

        (await engine.ActivateJobAsync("t-assigned")).ShouldNotBeNull();
        engine.AssertActiveElements(key, "Review");
    }
}
