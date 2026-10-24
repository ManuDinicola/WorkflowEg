using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using Flowd.Engine.State;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>M4: timers (T-TMR-01..05), messages (T-MSG-01..05) and the event-based gateway (T-EVG-01).</summary>
public sealed class TimeAndMessageTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static string TimerProcess(string timer) => Process("p", Start(), timer, Service("After", "after"), End(), Flow("f1", "Start", "T"), Flow("f2", "T", "After"), Flow("f3", "After", "End"));

    [Fact]
    public async Task T_TMR_01_intermediate_timer_waits_for_its_duration()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(TimerProcess(CatchTimer("T", "PT1H")));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "T");
        engine.AdvanceTime(TimeSpan.FromMinutes(59));
        (await engine.RunTimersAsync()).ShouldBe(0);
        engine.AssertActiveElements(key, "T");

        engine.AdvanceTime(TimeSpan.FromMinutes(1));
        (await engine.RunTimersAsync()).ShouldBe(1);

        engine.AssertActiveElements(key, "After");
        (await engine.RunTimersAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task FR_TMR_001_timer_date_and_expressions_are_supported()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("date", Start(), CatchTimer("T", "2026-10-24T13:00:00Z", "timeDate"), End(), Flow("f1", "Start", "T"), Flow("f2", "T", "End")));
        await engine.DeployAsync(Process("expr", Start(), CatchTimer("T", "= wait"), End(), Flow("f1", "Start", "T"), Flow("f2", "T", "End")));
        var byDate = await engine.StartAsync("date");
        var byExpression = await engine.StartAsync("expr", Json("{\"wait\":\"PT30M\"}"));

        engine.AdvanceTime(TimeSpan.FromMinutes(30));
        await engine.RunTimersAsync();
        engine.AssertCompleted(byExpression);
        engine.State(byDate).State.ShouldBe(InstanceState.Active);

        engine.AdvanceTime(TimeSpan.FromMinutes(30));
        await engine.RunTimersAsync();
        engine.AssertCompleted(byDate);
    }

    [Fact]
    public async Task T_TMR_02_interrupting_timer_boundary_terminates_the_task_and_continues_on_the_boundary_path()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Service("Work", "work"),
            BoundaryTimer("Late", "Work", "PT10M"),
            Service("Escalate", "escalate"),
            End("E1"),
            End("E2"),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "E1"),
            Flow("f3", "Late", "Escalate"),
            Flow("f4", "Escalate", "E2")));
        var key = await engine.StartAsync("p");
        var job = (await engine.ActivateJobAsync("work"))!;

        engine.AdvanceTime(TimeSpan.FromMinutes(10));
        await engine.RunTimersAsync();

        engine.AssertActiveElements(key, "Escalate");
        engine.State(key).Jobs[job.Key].State.ShouldBe(JobState.Canceled);
        TestEngine.ConflictCode(await engine.CompleteJobAsync(job.Key)).ShouldBe("JOB_NOT_ACTIVATED");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("escalate"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task T_TMR_03_non_interrupting_cycle_boundary_fires_three_times_while_the_task_continues()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Service("Work", "work"),
            BoundaryTimer("Remind", "Work", "R3/PT10M", "timeCycle", cancel: false),
            Service("Notify", "notify"),
            End("E1"),
            End("E2"),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "E1"),
            Flow("f3", "Remind", "Notify"),
            Flow("f4", "Notify", "E2")));
        var key = await engine.StartAsync("p");

        for (var i = 0; i < 5; i++)
        {
            engine.AdvanceTime(TimeSpan.FromMinutes(10));
            await engine.RunTimersAsync();
        }

        engine.State(key).Jobs.Values.Count(j => j.Type == "notify").ShouldBe(3);
        engine.State(key).Elements.Values.Single(e => e.ElementId == "Work").State.ShouldBe(ElementState.Active);
        engine.State(key).Timers.Values.ShouldAllBe(t => !t.IsActive);
    }

    [Fact]
    public async Task T_TMR_03_cycle_re_arming_is_drift_free()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), Service("Work", "work"), BoundaryTimer("Tick", "Work", "R/PT10M", "timeCycle", cancel: false), End("E1"), End("E2"), Flow("f1", "Start", "Work"), Flow("f2", "Work", "E1"), Flow("f3", "Tick", "E2")));
        var key = await engine.StartAsync("p");
        var firstDue = engine.State(key).Timers.Values.Single().DueAt;

        engine.AdvanceTime(TimeSpan.FromMinutes(17));
        await engine.RunTimersAsync();

        engine.State(key).Timers.Values.Single(t => t.IsActive).DueAt.ShouldBe(firstDue + TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task T_TMR_04_timers_survive_a_restart_and_overdue_timers_fire_on_startup()
    {
        var first = new TestEngine();
        await first.DeployAsync(TimerProcess(CatchTimer("T", "PT1H")));
        var key = await first.StartAsync("p");

        first.AdvanceTime(TimeSpan.FromHours(5));
        var restarted = new TestEngine(store: first.Store, time: first.Time);
        (await restarted.RunTimersAsync()).ShouldBe(1);

        restarted.AssertActiveElements(key, "After");
    }

    [Fact]
    public async Task T_TMR_05_two_nodes_fire_each_timer_exactly_once()
    {
        var nodeA = new TestEngine();
        await nodeA.DeployAsync(TimerProcess(CatchTimer("T", "PT1H")));
        var key = await nodeA.StartAsync("p");
        var nodeB = new TestEngine(store: nodeA.Store, time: nodeA.Time);
        nodeA.AdvanceTime(TimeSpan.FromHours(1));

        var handled = await Task.WhenAll(nodeA.RunTimersAsync(), nodeB.RunTimersAsync());

        handled.Sum().ShouldBe(1);
        nodeA.HistoryTypes(key, "After").Count(t => t == "ELEMENT_ACTIVATING").ShouldBe(1);
    }

    [Fact]
    public async Task FR_TMR_005_timer_start_events_create_instances_and_a_new_version_replaces_the_old_trigger()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", StartTimer("S", "R2/PT1H", "timeCycle"), Service("A", "a"), End(), Flow("f1", "S", "A"), Flow("f2", "A", "End")));

        engine.AdvanceTime(TimeSpan.FromHours(1));
        await engine.RunTimersAsync();
        engine.Store.Instances.Count.ShouldBe(1);

        var v2 = await engine.DeployAsync(Process("p", StartTimer("S", "R2/PT1H", "timeCycle"), Service("A", "a2"), End(), Flow("f1", "S", "A"), Flow("f2", "A", "End")));
        engine.Store.StartTimerDefinitions().ShouldBe([v2.Key]);
        engine.AdvanceTime(TimeSpan.FromHours(1));
        await engine.RunTimersAsync();
        engine.Store.Instances.Count.ShouldBe(2);
        engine.Store.Instances.Values.Last().DefinitionKey.ShouldBe(v2.Key);

        engine.AdvanceTime(TimeSpan.FromHours(3));
        await engine.RunTimersAsync();
        engine.Store.Instances.Count.ShouldBe(3);
    }

    private static string MessageProcess() => ProcessWithMessages(
        "p",
        MessageDef("M1", "payment", "= orderId"),
        Start(),
        CatchMessage("Wait", "M1"),
        End(),
        Flow("f1", "Start", "Wait"),
        Flow("f2", "Wait", "End"));

    [Fact]
    public async Task T_MSG_01_a_message_published_before_the_subscription_is_buffered_and_correlated_later()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("pre", Start(), Manual("M"), End(), Flow("f1", "Start", "M"), Flow("f2", "M", "End")));
        await engine.DeployAsync(MessageProcess());
        var published = TestEngine.Ok(await engine.PublishAsync("payment", "order-1", Json("{\"paid\":true}"), TimeSpan.FromMinutes(1)));
        published.Correlations.ShouldBe(0);

        var key = await engine.StartAsync("p", Json("{\"orderId\":\"order-1\"}"));

        engine.AssertCompleted(key);
        engine.Variables(key)["paid"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task T_MSG_02_a_message_with_an_unmatched_key_has_no_effect_and_expires()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(MessageProcess());
        var key = await engine.StartAsync("p", Json("{\"orderId\":\"order-1\"}"));

        TestEngine.Ok(await engine.PublishAsync("payment", "other", null, TimeSpan.FromMinutes(1))).Correlations.ShouldBe(0);

        engine.AssertActiveElements(key, "Wait");
        engine.AdvanceTime(TimeSpan.FromMinutes(2));
        (await engine.Store.DeleteExpiredAsync(engine.Time.GetUtcNow(), CancellationToken.None)).ShouldBe(1);
        engine.Store.BufferedCount.ShouldBe(0);
    }

    [Fact]
    public async Task T_MSG_03_a_duplicate_message_id_is_rejected_while_buffered()
    {
        var engine = new TestEngine();
        TestEngine.Ok(await engine.PublishAsync("payment", "k", null, TimeSpan.FromMinutes(1), "m-1"));

        var duplicate = await engine.PublishAsync("payment", "k", null, TimeSpan.FromMinutes(1), "m-1");

        TestEngine.ConflictCode(duplicate).ShouldBe("MESSAGE_ALREADY_EXISTS");
        engine.AdvanceTime(TimeSpan.FromMinutes(2));
        await engine.Store.DeleteExpiredAsync(engine.Time.GetUtcNow(), CancellationToken.None);
        TestEngine.Ok(await engine.PublishAsync("payment", "k", null, TimeSpan.FromMinutes(1), "m-1"));
    }

    [Fact]
    public async Task T_MSG_04_a_message_start_event_creates_an_instance_with_the_message_variables()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages("p", MessageDef("M1", "order-placed"), StartMessage("S", "M1"), Service("A", "a"), End(), Flow("f1", "S", "A"), Flow("f2", "A", "End")));

        var published = TestEngine.Ok(await engine.PublishAsync("order-placed", "any", Json("{\"item\":\"book\"}")));

        published.Correlations.ShouldBe(1);
        var instance = engine.Store.Instances.Values.ShouldHaveSingleItem();
        instance.Variables.Visible(instance.InstanceKey)["item"]!.GetValue<string>().ShouldBe("book");
        TestEngine.Ok(await engine.PublishAsync("order-placed", "any", null, TimeSpan.Zero)).Correlations.ShouldBe(1);
    }

    [Fact]
    public async Task T_MSG_05_a_message_reaches_at_most_one_instance_per_definition()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(MessageProcess());
        await engine.DeployAsync(ProcessWithMessages("q", MessageDef("M2", "payment", "= orderId"), Start(), CatchMessage("Wait", "M2"), End(), Flow("f1", "Start", "Wait"), Flow("f2", "Wait", "End")));
        var p1 = await engine.StartAsync("p", Json("{\"orderId\":\"o\"}"));
        var p2 = await engine.StartAsync("p", Json("{\"orderId\":\"o\"}"));
        var q1 = await engine.StartAsync("q", Json("{\"orderId\":\"o\"}"));

        var published = TestEngine.Ok(await engine.PublishAsync("payment", "o", null, TimeSpan.FromMinutes(1)));

        published.Correlations.ShouldBe(2);
        engine.State(p1).State.ShouldBe(InstanceState.Completed);
        engine.State(p2).State.ShouldBe(InstanceState.Active);
        engine.State(q1).State.ShouldBe(InstanceState.Completed);
        await engine.StartAsync("p", Json("{\"orderId\":\"o\"}"));
        engine.State(p2).State.ShouldBe(InstanceState.Active);
    }

    [Fact]
    public async Task FR_MSG_004_receive_task_and_message_boundary_events_work()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "p",
            MessageDef("M1", "reply", "= id"),
            Start(),
            ReceiveTask("Receive", "M1"),
            End("E1"),
            Flow("f1", "Start", "Receive"),
            Flow("f2", "Receive", "E1")));
        await engine.DeployAsync(ProcessWithMessages(
            "b",
            MessageDef("M2", "cancel", "= id"),
            Start(),
            Service("Work", "work"),
            BoundaryMessage("Cancelled", "Work", "M2"),
            End("E1"),
            End("E2"),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "E1"),
            Flow("f3", "Cancelled", "E2")));
        var receive = await engine.StartAsync("p", Json("{\"id\":\"x\"}"));
        var boundary = await engine.StartAsync("b", Json("{\"id\":\"y\"}"));

        TestEngine.Ok(await engine.PublishAsync("reply", "x", Json("{\"answer\":42}")));
        TestEngine.Ok(await engine.PublishAsync("cancel", "y"));

        engine.AssertCompleted(receive);
        engine.Variables(receive)["answer"]!.GetValue<int>().ShouldBe(42);
        engine.AssertCompleted(boundary);
        engine.State(boundary).Jobs.Values.Single().State.ShouldBe(JobState.Canceled);
    }

    [Fact]
    public async Task T_EVG_01_event_based_gateway_first_trigger_wins_and_removes_the_loser()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "p",
            MessageDef("M1", "answer", "= id"),
            Start(),
            EventGateway("G"),
            CatchMessage("Got", "M1"),
            CatchTimer("Timeout", "PT1H"),
            End("E1"),
            End("E2"),
            Flow("f0", "Start", "G"),
            Flow("f1", "G", "Got"),
            Flow("f2", "G", "Timeout"),
            Flow("f3", "Got", "E1"),
            Flow("f4", "Timeout", "E2")));
        var byMessage = await engine.StartAsync("p", Json("{\"id\":\"a\"}"));
        var byTimer = await engine.StartAsync("p", Json("{\"id\":\"b\"}"));

        TestEngine.Ok(await engine.PublishAsync("answer", "a"));
        engine.AdvanceTime(TimeSpan.FromHours(1));
        await engine.RunTimersAsync();

        engine.AssertCompleted(byMessage);
        engine.HistoryTypes(byMessage, "Timeout").ShouldNotContain("ELEMENT_COMPLETED");
        engine.State(byMessage).Timers.Values.ShouldAllBe(t => !t.IsActive);
        engine.AssertCompleted(byTimer);
        engine.State(byTimer).Subscriptions.ShouldBeEmpty();
        engine.HistoryTypes(byTimer, "Got").ShouldNotContain("ELEMENT_COMPLETED");
    }

    [Fact]
    public async Task FR_DEP_004_invalid_timer_and_boundary_definitions_are_rejected()
    {
        var engine = new TestEngine();

        var bad = await engine.TryDeployAsync(Process(
            "p",
            Start(),
            Service("Work", "work"),
            BoundaryTimer("B1", "Work", "R3/PT5M", "timeCycle"),
            BoundaryTimer("B2", "Nowhere", "PT5M"),
            CatchTimer("T", "not-a-duration"),
            End(),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "T"),
            Flow("f3", "T", "End"),
            Flow("f4", "B1", "End"),
            Flow("f5", "B2", "End")));

        var text = TestEngine.Describe(bad is Engine.Results.Failure f ? f : throw new InvalidOperationException("expected failure"));
        text.ShouldContain("INVALID_TIMER");
        text.ShouldContain("INVALID_BOUNDARY_EVENT");
    }
}
