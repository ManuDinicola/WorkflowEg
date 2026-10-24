using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using Flowd.Engine.State;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>M8: multi-instance completion condition (FR-EXEC-012).</summary>
public sealed class MultiInstanceCompletionTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public async Task FR_EXEC_012_a_true_completion_condition_cancels_the_remaining_instances_and_completes_the_body()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            MiService("Each", "each", sequential: false, "= items", "item", "results", "= item", "= count(results) >= 1"),
            Service("After", "after"),
            End(),
            Flow("f1", "Start", "Each"),
            Flow("f2", "Each", "After"),
            Flow("f3", "After", "End")));
        var key = await engine.StartAsync("p", Json("{\"items\":[1,2,3]}"));

        var first = (await engine.ActivateJobAsync("each"))!;
        TestEngine.Ok(await engine.CompleteJobAsync(first.Key));

        engine.AssertActiveElements(key, "After");
        engine.State(key).Elements.Values.Count(e => e.ElementId == "Each" && e.State == ElementState.Terminated).ShouldBeGreaterThanOrEqualTo(2);
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("after"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_EXEC_012_a_false_completion_condition_lets_all_instances_run()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            MiService("Each", "each", sequential: true, "= items", "item", "results", "= item", "= false"),
            End(),
            Flow("f1", "Start", "Each"),
            Flow("f2", "Each", "End")));
        var key = await engine.StartAsync("p", Json("{\"items\":[1,2]}"));

        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("each"))!.Key));
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("each"))!.Key));

        engine.AssertCompleted(key);
        engine.Variables(key)["results"]!.AsArray().Count.ShouldBe(2);
    }
}
