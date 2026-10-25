using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>FR-SIG-002: a signal end event broadcasts its signal when the path ends.</summary>
public sealed class SignalEndEventTests
{
    [Fact]
    public async Task FR_SIG_002_a_signal_end_event_wakes_instances_waiting_for_the_signal()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages("waiter", SignalDef("S1", "done"), Start(), CatchSignal("Wait", "S1"), End(), Flow("f1", "Start", "Wait"), Flow("f2", "Wait", "End")));
        await engine.DeployAsync(ProcessWithMessages("sender", SignalDef("S1", "done"), Start(), "<bpmn:endEvent id=\"EndSignal\"><bpmn:signalEventDefinition signalRef=\"S1\"/></bpmn:endEvent>", Flow("f1", "Start", "EndSignal")));
        var waiter = await engine.StartAsync("waiter");
        engine.AssertActiveElements(waiter, "Wait");

        var sender = await engine.StartAsync("sender");

        engine.AssertCompleted(sender);
        engine.AssertCompleted(waiter);
    }
}
