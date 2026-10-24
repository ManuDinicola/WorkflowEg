using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>M8: escalation events (FR-ESC-001).</summary>
public sealed class EscalationTests
{
    private static string Sub(string boundary, bool endEvent = false) => ProcessWithMessages(
        "p",
        EscalationDef("E1", "LATE"),
        Start(),
        SubProcess(
            "Sub",
            string.Empty,
            Start("SS"),
            endEvent ? Manual("Pre") : ThrowEscalation("Throw", "E1"),
            Service("Inner", "inner"),
            endEvent ? EscalationEnd("EscEnd", "E1") : End("SE"),
            Flow("s1", "SS", endEvent ? "Pre" : "Throw"),
            Flow("s2", endEvent ? "Pre" : "Throw", endEvent ? "EscEnd" : "Inner"),
            endEvent ? string.Empty : Flow("s3", "Inner", "SE")),
        boundary,
        Service("Handle", "handle"),
        End("Done"),
        Flow("f1", "Start", "Sub"),
        Flow("f2", "Sub", "Done"),
        Flow("f3", "Caught", "Handle"),
        Flow("f4", "Handle", "Done"));

    [Fact]
    public async Task FR_ESC_001_a_non_interrupting_boundary_runs_beside_the_thrower()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Sub(BoundaryEscalation("Caught", "Sub", "E1", cancel: false)));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "Sub", "Inner", "Handle");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("handle"))!.Key));
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("inner"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_ESC_001_an_interrupting_boundary_cancels_the_sub_process()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Sub(BoundaryEscalation("Caught", "Sub")));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "Handle");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("handle"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_ESC_001_an_escalation_end_event_is_caught_and_a_catch_all_boundary_matches_any_code()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Sub(BoundaryEscalation("Caught", "Sub"), endEvent: true));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "Handle");
        engine.OpenIncidents(key).ShouldBeEmpty();
    }

    [Fact]
    public async Task FR_ESC_001_an_uncaught_escalation_is_ignored_and_the_process_continues()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "p",
            EscalationDef("E1", "LATE"),
            Start(),
            ThrowEscalation("Throw", "E1"),
            Service("Work", "work"),
            End(),
            Flow("f1", "Start", "Throw"),
            Flow("f2", "Throw", "Work"),
            Flow("f3", "Work", "End")));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "Work");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("work"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_ESC_001_an_escalation_event_sub_process_handles_the_escalation()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "p",
            EscalationDef("E1", "LATE"),
            Start(),
            ThrowEscalation("Throw", "E1"),
            Service("Work", "work"),
            End(),
            EventSubProcess(
                "Esp",
                "<bpmn:startEvent id=\"EspStart\" isInterrupting=\"false\"><bpmn:escalationEventDefinition escalationRef=\"E1\"/></bpmn:startEvent>",
                Service("Handle", "handle"),
                End("EspEnd"),
                Flow("e1", "EspStart", "Handle"),
                Flow("e2", "Handle", "EspEnd")),
            Flow("f1", "Start", "Throw"),
            Flow("f2", "Throw", "Work"),
            Flow("f3", "Work", "End")));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "Work", "Esp", "Handle");
    }

    [Fact]
    public async Task FR_ESC_001_an_escalation_catch_event_is_rejected_at_deployment()
    {
        var engine = new TestEngine();
        var result = await engine.TryDeployAsync(ProcessWithMessages(
            "p",
            EscalationDef("E1", "LATE"),
            Start(),
            "<bpmn:intermediateCatchEvent id=\"Bad\"><bpmn:escalationEventDefinition escalationRef=\"E1\"/></bpmn:intermediateCatchEvent>",
            End(),
            Flow("f1", "Start", "Bad"),
            Flow("f2", "Bad", "End")));

        TestEngine.Describe(result is Engine.Results.Failure f ? f : throw new InvalidOperationException("expected failure")).ShouldContain("INVALID_EVENT_DEFINITION");
    }
}
