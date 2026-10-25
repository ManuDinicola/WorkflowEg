using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>FR-CMP-001: simplified compensation.</summary>
public sealed class CompensationTests
{
    private static string Saga(string thrower) => Process(
        "p",
        Start(),
        Service("Book", "book"),
        "<bpmn:boundaryEvent id=\"BookComp\" attachedToRef=\"Book\"><bpmn:compensateEventDefinition/></bpmn:boundaryEvent>",
        Service("CancelBook", "cancel-book").Replace("<bpmn:serviceTask id=\"CancelBook\"", "<bpmn:serviceTask id=\"CancelBook\" isForCompensation=\"true\"", StringComparison.Ordinal),
        "<bpmn:association id=\"as1\" sourceRef=\"BookComp\" targetRef=\"CancelBook\"/>",
        Service("Pay", "pay"),
        thrower,
        End(),
        Flow("f1", "Start", "Book"),
        Flow("f2", "Book", "Pay"),
        Flow("f3", "Pay", "Throw"),
        Flow("f4", "Throw", "End"));

    [Fact]
    public async Task FR_CMP_001_a_compensation_throw_event_runs_the_handlers_of_completed_activities()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Saga("<bpmn:intermediateThrowEvent id=\"Throw\"><bpmn:compensateEventDefinition/></bpmn:intermediateThrowEvent>"));
        var key = await engine.StartAsync("p");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("book"))!.Key));
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("pay"))!.Key));

        var handler = await engine.ActivateJobAsync("cancel-book");

        handler.ShouldNotBeNull();
        TestEngine.Ok(await engine.CompleteJobAsync(handler.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_CMP_001_activities_that_never_completed_are_not_compensated()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Saga("<bpmn:intermediateThrowEvent id=\"Throw\"><bpmn:compensateEventDefinition/></bpmn:intermediateThrowEvent>"));
        await engine.StartAsync("p");

        (await engine.ActivateJobAsync("cancel-book")).ShouldBeNull();
    }

    [Fact]
    public async Task FR_CMP_001_a_compensation_end_event_starts_the_handlers_before_the_instance_ends()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Saga("<bpmn:endEvent id=\"Throw\"><bpmn:compensateEventDefinition/></bpmn:endEvent>").Replace("<bpmn:endEvent id=\"End\"/>", string.Empty, StringComparison.Ordinal).Replace("<bpmn:sequenceFlow id=\"f4\" sourceRef=\"Throw\" targetRef=\"End\"/>", string.Empty, StringComparison.Ordinal));
        var key = await engine.StartAsync("p");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("book"))!.Key));
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("pay"))!.Key));

        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("cancel-book"))!.Key));

        engine.AssertCompleted(key);
    }
}
