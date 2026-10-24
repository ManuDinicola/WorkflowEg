using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>M8: link events (FR-LNK-001).</summary>
public sealed class LinkEventTests
{
    [Fact]
    public async Task FR_LNK_001_a_link_throw_event_continues_at_the_matching_catch_event()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            ThrowLink("Out", "L1"),
            Service("Skipped", "skipped"),
            CatchLink("In", "L1"),
            Service("After", "after"),
            End(),
            Flow("f1", "Start", "Out"),
            Flow("f2", "In", "After"),
            Flow("f3", "After", "End"),
            Flow("f4", "Skipped", "End")));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "After");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("after"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_LNK_001_a_link_inside_a_sub_process_stays_in_that_scope()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            SubProcess("Sub", string.Empty, Start("SS"), ThrowLink("Out", "L"), CatchLink("In", "L"), Service("Work", "work"), End("SE"), Flow("s1", "SS", "Out"), Flow("s2", "In", "Work"), Flow("s3", "Work", "SE")),
            End(),
            Flow("f1", "Start", "Sub"),
            Flow("f2", "Sub", "End")));
        var key = await engine.StartAsync("p");

        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("work"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_LNK_001_a_link_throw_without_a_matching_catch_is_rejected_at_deployment()
    {
        var engine = new TestEngine();
        var bad = await engine.TryDeployAsync(Process("p", Start(), ThrowLink("Out", "L1"), End(), Flow("f1", "Start", "Out"), Flow("f2", "Out", "End")));

        TestEngine.Describe(bad is Engine.Results.Failure f ? f : throw new InvalidOperationException("expected failure")).ShouldContain("INVALID_EVENT_DEFINITION");
    }
}
