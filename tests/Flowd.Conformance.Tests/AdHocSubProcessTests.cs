using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>FR-ADH-001: ad-hoc sub-process starts every activity without an incoming flow.</summary>
public sealed class AdHocSubProcessTests
{
    [Fact]
    public async Task FR_ADH_001_all_free_activities_start_and_the_scope_ends_when_all_are_done()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            "<bpmn:adHocSubProcess id=\"Ad\">" + Service("A", "a") + Service("B", "b") + Service("C", "c") + Flow("x1", "A", "C") + "</bpmn:adHocSubProcess>",
            End(),
            Flow("f1", "Start", "Ad"),
            Flow("f2", "Ad", "End")));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "Ad", "A", "B");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("a"))!.Key));
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("b"))!.Key));
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("c"))!.Key));

        engine.AssertCompleted(key);
    }
}
