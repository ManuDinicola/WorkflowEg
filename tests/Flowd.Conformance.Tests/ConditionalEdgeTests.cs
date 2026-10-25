using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>FR-CND-001: conditional events in event sub-processes, non-interrupting boundaries and nested scopes.</summary>
public sealed class ConditionalEdgeTests
{
    private const string Cond = "<bpmn:conditionalEventDefinition><bpmn:condition>= alarm = true</bpmn:condition></bpmn:conditionalEventDefinition>";

    [Fact]
    public async Task FR_CND_001_an_interrupting_event_sub_process_starts_when_the_condition_becomes_true()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Service("Work", "work"),
            End(),
            EventSubProcess("Esp", $"<bpmn:startEvent id=\"EspStart\" isInterrupting=\"true\">{Cond}</bpmn:startEvent>", Service("Handle", "handle"), End("EspEnd"), Flow("e1", "EspStart", "Handle"), Flow("e2", "Handle", "EspEnd")),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "End")));
        var key = await engine.StartAsync("p", new JsonObject { ["alarm"] = false });
        engine.AssertActiveElements(key, "Work");

        TestEngine.Ok(await engine.SetVariablesAsync(key, new JsonObject { ["alarm"] = true }));

        engine.AssertActiveElements(key, "Esp", "Handle");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("handle"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_CND_001_a_non_interrupting_boundary_event_keeps_the_activity_and_fires_once()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Service("Work", "work"),
            $"<bpmn:boundaryEvent id=\"Cond\" attachedToRef=\"Work\" cancelActivity=\"false\">{Cond}</bpmn:boundaryEvent>",
            Service("Notify", "notify"),
            End("Done"),
            End("NotifyEnd"),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "Done"),
            Flow("f3", "Cond", "Notify"),
            Flow("f4", "Notify", "NotifyEnd")));
        var key = await engine.StartAsync("p", new JsonObject { ["alarm"] = false });

        TestEngine.Ok(await engine.SetVariablesAsync(key, new JsonObject { ["alarm"] = true }));
        TestEngine.Ok(await engine.SetVariablesAsync(key, new JsonObject { ["alarm"] = false }));
        TestEngine.Ok(await engine.SetVariablesAsync(key, new JsonObject { ["alarm"] = true }));

        engine.AssertActiveElements(key, "Work", "Notify");
        (await engine.ActivateJobAsync("notify"))!.ShouldNotBeNull();
        (await engine.ActivateJobAsync("notify")).ShouldBeNull(); // only one notify job was created
    }

    [Fact]
    public async Task FR_CND_001_a_condition_that_cannot_be_evaluated_does_not_fire_or_crash()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), $"<bpmn:intermediateCatchEvent id=\"Wait\">{Cond}</bpmn:intermediateCatchEvent>", End(), Flow("f1", "Start", "Wait"), Flow("f2", "Wait", "End")));

        var key = await engine.StartAsync("p"); // the variable alarm does not exist yet

        engine.AssertActiveElements(key, "Wait");
        TestEngine.Ok(await engine.SetVariablesAsync(key, new JsonObject { ["alarm"] = true }));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_CND_001_a_catch_event_inside_a_sub_process_sees_the_variables_of_its_scope()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            SubProcess("Sub", string.Empty, Start("SS"), $"<bpmn:intermediateCatchEvent id=\"Wait\">{Cond}</bpmn:intermediateCatchEvent>", End("SE"), Flow("s1", "SS", "Wait"), Flow("s2", "Wait", "SE")),
            End(),
            Flow("f1", "Start", "Sub"),
            Flow("f2", "Sub", "End")));
        var key = await engine.StartAsync("p");
        engine.AssertActiveElements(key, "Sub", "Wait");

        TestEngine.Ok(await engine.SetVariablesAsync(key, new JsonObject { ["alarm"] = true }));

        engine.AssertCompleted(key);
    }
}
