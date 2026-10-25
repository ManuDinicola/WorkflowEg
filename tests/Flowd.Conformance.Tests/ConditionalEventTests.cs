using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>FR-CND-001: conditional catch and boundary events.</summary>
public sealed class ConditionalEventTests
{
    private const string Cond = "<bpmn:conditionalEventDefinition><bpmn:condition>= approved = true</bpmn:condition></bpmn:conditionalEventDefinition>";

    [Fact]
    public async Task FR_CND_001_a_catch_event_waits_until_the_condition_becomes_true()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), $"<bpmn:intermediateCatchEvent id=\"Wait\">{Cond}</bpmn:intermediateCatchEvent>", End(), Flow("f1", "Start", "Wait"), Flow("f2", "Wait", "End")));
        var key = await engine.StartAsync("p", new JsonObject { ["approved"] = false });

        engine.AssertActiveElements(key, "Wait");
        TestEngine.Ok(await engine.SetVariablesAsync(key, new JsonObject { ["approved"] = true }));

        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_CND_001_a_catch_event_passes_at_once_when_the_condition_already_holds()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), $"<bpmn:intermediateCatchEvent id=\"Wait\">{Cond}</bpmn:intermediateCatchEvent>", End(), Flow("f1", "Start", "Wait"), Flow("f2", "Wait", "End")));

        engine.AssertCompleted(await engine.StartAsync("p", new JsonObject { ["approved"] = true }));
    }

    [Fact]
    public async Task FR_CND_001_an_interrupting_boundary_event_cancels_the_activity_when_the_condition_becomes_true()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Service("Work", "work"),
            $"<bpmn:boundaryEvent id=\"Cond\" attachedToRef=\"Work\" cancelActivity=\"true\">{Cond}</bpmn:boundaryEvent>",
            End("Done"),
            End("Cancelled"),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "Done"),
            Flow("f3", "Cond", "Cancelled")));
        var key = await engine.StartAsync("p", new JsonObject { ["approved"] = false });
        engine.AssertActiveElements(key, "Work");

        TestEngine.Ok(await engine.SetVariablesAsync(key, new JsonObject { ["approved"] = true }));

        engine.AssertCompleted(key);
    }
}
