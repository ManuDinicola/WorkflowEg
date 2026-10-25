using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>Edge cases of listeners, compensation and ad-hoc sub-processes.</summary>
public sealed class Batch2EdgeTests
{
    [Fact]
    public async Task FR_CMP_001_throwing_compensation_twice_runs_each_handler_once()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Service("Book", "book"),
            "<bpmn:boundaryEvent id=\"BookComp\" attachedToRef=\"Book\"><bpmn:compensateEventDefinition/></bpmn:boundaryEvent>",
            Service("Cancel", "cancel").Replace("<bpmn:serviceTask id=\"Cancel\"", "<bpmn:serviceTask id=\"Cancel\" isForCompensation=\"true\"", StringComparison.Ordinal),
            "<bpmn:association id=\"as1\" sourceRef=\"BookComp\" targetRef=\"Cancel\"/>",
            "<bpmn:intermediateThrowEvent id=\"T1\"><bpmn:compensateEventDefinition/></bpmn:intermediateThrowEvent>",
            "<bpmn:intermediateThrowEvent id=\"T2\"><bpmn:compensateEventDefinition/></bpmn:intermediateThrowEvent>",
            Service("Hold", "hold"),
            End(),
            Flow("f1", "Start", "Book"),
            Flow("f2", "Book", "T1"),
            Flow("f3", "T1", "T2"),
            Flow("f4", "T2", "Hold"),
            Flow("f5", "Hold", "End")));
        await engine.StartAsync("p");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("book"))!.Key));

        (await engine.ActivateJobAsync("cancel")).ShouldNotBeNull();
        (await engine.ActivateJobAsync("cancel")).ShouldBeNull();
    }

    [Fact]
    public async Task FR_LST_001_a_user_task_completed_listener_fires_when_the_task_is_completed()
    {
        var engine = new TestEngine();
        const string inner = "<flowd:taskListeners><flowd:listener event=\"completed\" type=\"t-done\"/></flowd:taskListeners>";
        await engine.DeployAsync(Process("p", Start(), UserTask("Review", inner: inner), End(), Flow("f1", "Start", "Review"), Flow("f2", "Review", "End")));
        var key = await engine.StartAsync("p");
        (await engine.ActivateJobAsync("t-done")).ShouldBeNull();

        TestEngine.Ok(await engine.CompleteTaskAsync(engine.UserTasks(key).Single().Key));

        (await engine.ActivateJobAsync("t-done")).ShouldNotBeNull();
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_LST_001_an_unknown_listener_event_is_rejected_at_deployment()
    {
        var engine = new TestEngine();
        const string inner = "<flowd:executionListeners><flowd:listener event=\"explode\" type=\"x\"/></flowd:executionListeners>";

        var bad = await engine.TryDeployAsync(Process("p", Start(), Service("A", "a", inner: inner), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));

        TestEngine.Describe(bad is Engine.Results.Failure f ? f : throw new InvalidOperationException("expected failure")).ShouldContain("INVALID_EVENT_DEFINITION");
    }

    [Fact]
    public async Task FR_ADH_001_an_ad_hoc_sub_process_without_inner_activities_completes_at_once()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), "<bpmn:adHocSubProcess id=\"Ad\"/>", End(), Flow("f1", "Start", "Ad"), Flow("f2", "Ad", "End")));

        engine.AssertCompleted(await engine.StartAsync("p"));
    }
}
