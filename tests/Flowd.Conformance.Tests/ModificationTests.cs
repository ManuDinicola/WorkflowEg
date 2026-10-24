using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>Process instance modification and start instructions (FR-OPS-001, FR-OPS-002).</summary>
public sealed class ModificationTests
{
    private static string Linear() => Process(
        "p",
        Start(),
        Service("A", "a"),
        Service("B", "b"),
        Service("C", "c"),
        End(),
        Flow("f1", "Start", "A"),
        Flow("f2", "A", "B"),
        Flow("f3", "B", "C"),
        Flow("f4", "C", "End"));

    [Fact]
    public async Task FR_OPS_001_moving_the_token_skips_a_step()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Linear());
        var key = await engine.StartAsync("p");
        var waitingOnA = engine.State(key).LiveElements().Single(e => e.ElementId == "A");

        TestEngine.Ok(await engine.ModifyAsync(key, [waitingOnA.Key], ["C"]));

        engine.AssertActiveElements(key, "C");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("c"))!.Key));
        engine.AssertCompleted(key);
        (await engine.ActivateJobAsync("a")).ShouldBeNull();
    }

    [Fact]
    public async Task FR_OPS_001_a_token_can_be_added_without_ending_the_existing_one_and_variables_are_set()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Linear());
        var key = await engine.StartAsync("p");

        TestEngine.Ok(await engine.ModifyAsync(key, [], ["B"], JsonNode.Parse("""{"fixed":true}""")!.AsObject()));

        engine.AssertActiveElements(key, "A", "B");
        engine.Variables(key)["fixed"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task FR_OPS_001_unknown_or_ended_elements_and_boundaries_are_rejected()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Linear());
        var key = await engine.StartAsync("p");

        TestEngine.Failed(await engine.ModifyAsync(key, [123456], [])).ShouldBeTrue();
        TestEngine.Failed(await engine.ModifyAsync(key, [], ["Nope"])).ShouldBeTrue();
        engine.AssertActiveElements(key, "A");
    }

    [Fact]
    public async Task FR_OPS_001_a_finished_instance_cannot_be_modified()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Linear());
        var key = await engine.StartAsync("p");
        TestEngine.Ok(await engine.CancelAsync(key));

        TestEngine.ConflictCode(await engine.ModifyAsync(key, [], ["B"])).ShouldBe("INSTANCE_NOT_ACTIVE");
    }

    [Fact]
    public async Task FR_OPS_002_an_instance_can_start_at_any_element_of_the_process()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Linear());

        var started = await engine.TryStartAsync("p", startElementId: "B");

        var key = TestEngine.Ok(started).InstanceKey;
        engine.AssertActiveElements(key, "B");
        TestEngine.Failed(await engine.TryStartAsync("p", startElementId: "Nope")).ShouldBeTrue();
    }
}
