using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using Flowd.Engine.State;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>M6: call activities (T-CALL), multi-instance (T-MI) and event sub-processes (T-ESP).</summary>
public sealed class CompositionTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static async Task<TestEngine> WithChildAsync()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("child", Start(), Service("Work", "child-work"), End(), Flow("c1", "Start", "Work"), Flow("c2", "Work", "End")));
        return engine;
    }

    [Fact]
    public async Task T_CALL_01_a_call_activity_starts_a_child_and_the_parent_resumes_when_it_completes()
    {
        var engine = await WithChildAsync();
        await engine.DeployAsync(Process(
            "parent",
            Start(),
            CallActivity("Call", "child"),
            Service("After", "after"),
            End(),
            Flow("f1", "Start", "Call"),
            Flow("f2", "Call", "After"),
            Flow("f3", "After", "End")));
        var parent = await engine.StartAsync("parent", Json("{\"order\":7}"));

        var job = (await engine.ActivateJobAsync("child-work")).ShouldNotBeNull();
        var childKey = job.InstanceKey;
        engine.State(childKey).ParentInstanceKey.ShouldBe(parent);
        engine.Variables(childKey)["order"]!.GetValue<int>().ShouldBe(7);
        engine.AssertActiveElements(parent, "Call");

        TestEngine.Ok(await engine.CompleteJobAsync(job.Key, Json("{\"result\":\"ok\"}")));

        engine.AssertCompleted(childKey);
        engine.AssertActiveElements(parent, "After");
        engine.Variables(parent)["result"]!.GetValue<string>().ShouldBe("ok");
    }

    [Fact]
    public async Task T_CALL_01_propagation_flags_and_mappings_control_the_variables_in_both_directions()
    {
        var engine = await WithChildAsync();
        await engine.DeployAsync(Process(
            "parent",
            Start(),
            CallActivity("Call", "child", CallFlags(propagateChild: false, propagateParent: false, Mapping(Input("= order.id", "orderId"), Output("= result", "answer")))),
            End(),
            Flow("f1", "Start", "Call"),
            Flow("f2", "Call", "End")));
        var parent = await engine.StartAsync("parent", Json("{\"order\":{\"id\":\"A-1\"},\"secret\":1}"));

        var job = (await engine.ActivateJobAsync("child-work"))!;
        job.Variables.Select(p => p.Key).ShouldBe(["orderId"]);
        TestEngine.Ok(await engine.CompleteJobAsync(job.Key, Json("{\"result\":42,\"noise\":1}")));

        engine.AssertCompleted(parent);
        var variables = engine.Variables(parent);
        variables["answer"]!.GetValue<decimal>().ShouldBe(42m);
        variables.ContainsKey("noise").ShouldBeFalse();
        variables.ContainsKey("result").ShouldBeFalse();
    }

    [Fact]
    public async Task T_CALL_02_cancelling_the_parent_cancels_the_child_instances()
    {
        var engine = await WithChildAsync();
        await engine.DeployAsync(Process("parent", Start(), CallActivity("Call", "child"), End(), Flow("f1", "Start", "Call"), Flow("f2", "Call", "End")));
        var parent = await engine.StartAsync("parent");
        var childKey = (await engine.ActivateJobAsync("child-work"))!.InstanceKey;

        TestEngine.Ok(await engine.CancelAsync(parent));

        engine.AssertTerminated(parent);
        engine.AssertTerminated(childKey);
    }

    [Fact]
    public async Task FR_EXEC_011_a_missing_called_process_raises_an_incident_that_can_be_resolved_after_deploying_it()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("parent", Start(), CallActivity("Call", "child"), End(), Flow("f1", "Start", "Call"), Flow("f2", "Call", "End")));
        var parent = await engine.StartAsync("parent");

        var incident = engine.OpenIncidents(parent).ShouldHaveSingleItem();
        incident.ErrorType.ShouldBe("CALLED_ELEMENT_ERROR");
        await engine.DeployAsync(Process("child", Start(), End(), Flow("c1", "Start", "End")));
        TestEngine.Ok(await engine.ResolveIncidentAsync(incident.Key));

        engine.AssertCompleted(parent);
    }

    [Fact]
    public async Task FR_EXEC_011_an_unhandled_child_error_continues_at_the_call_activity_boundary()
    {
        var engine = await WithChildAsync();
        await engine.DeployAsync(ProcessWithMessages(
            "parent",
            ErrorDef("E1", "BOOM"),
            Start(),
            CallActivity("Call", "child"),
            BoundaryError("Caught", "Call", "E1"),
            End("Done"),
            End("Handled"),
            Flow("f1", "Start", "Call"),
            Flow("f2", "Call", "Done"),
            Flow("f3", "Caught", "Handled")));
        var parent = await engine.StartAsync("parent");
        var job = (await engine.ActivateJobAsync("child-work"))!;

        TestEngine.Ok(await engine.ThrowErrorAsync(job.Key, "BOOM"));

        engine.AssertTerminated(job.InstanceKey);
        engine.AssertCompleted(parent);
        engine.HistoryTypes(parent, "Handled").ShouldContain("ELEMENT_COMPLETED");
    }

    [Fact]
    public async Task FR_EXEC_011_cancelling_a_child_directly_raises_an_incident_on_the_parent()
    {
        var engine = await WithChildAsync();
        await engine.DeployAsync(Process("parent", Start(), CallActivity("Call", "child"), End(), Flow("f1", "Start", "Call"), Flow("f2", "Call", "End")));
        var parent = await engine.StartAsync("parent");
        var childKey = (await engine.ActivateJobAsync("child-work"))!.InstanceKey;

        TestEngine.Ok(await engine.CancelAsync(childKey));

        engine.OpenIncidents(parent).ShouldHaveSingleItem().ErrorType.ShouldBe("CALLED_ELEMENT_ERROR");
    }

    [Fact]
    public async Task T_MI_01_parallel_multi_instance_creates_all_jobs_and_keeps_the_input_order_in_the_output()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            MiService("Each", "each", sequential: false, "= items", "item", "results", "= doubled"),
            End(),
            Flow("f1", "Start", "Each"),
            Flow("f2", "Each", "End")));
        var key = await engine.StartAsync("p", Json("{\"items\":[\"a\",\"b\",\"c\"]}"));

        var jobs = new List<Application.Jobs.ActivatedJob>();
        for (var i = 0; i < 3; i++)
        {
            jobs.Add((await engine.ActivateJobAsync("each"))!);
        }

        jobs.Select(j => j.Variables["item"]!.GetValue<string>()).ShouldBe(["a", "b", "c"]);
        (await engine.ActivateJobAsync("each")).ShouldBeNull();
        foreach (var job in Enumerable.Reverse(jobs))
        {
            var item = job.Variables["item"]!.GetValue<string>();
            TestEngine.Ok(await engine.CompleteJobAsync(job.Key, Json($"{{\"doubled\":\"{item}{item}\"}}")));
        }

        engine.AssertCompleted(key);
        engine.Variables(key)["results"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["aa", "bb", "cc"]);
    }

    [Fact]
    public async Task T_MI_02_sequential_multi_instance_runs_one_iteration_at_a_time()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            MiService("Each", "each", sequential: true, "= items", "item", "results", "= item"),
            End(),
            Flow("f1", "Start", "Each"),
            Flow("f2", "Each", "End")));
        var key = await engine.StartAsync("p", Json("{\"items\":[1,2,3]}"));

        for (var expected = 1; expected <= 3; expected++)
        {
            var job = (await engine.ActivateJobAsync("each"))!;
            job.Variables["item"]!.GetValue<decimal>().ShouldBe(expected);
            (await engine.ActivateJobAsync("each")).ShouldBeNull();
            TestEngine.Ok(await engine.CompleteJobAsync(job.Key));
        }

        engine.AssertCompleted(key);
        engine.Variables(key)["results"]!.AsArray().Select(n => n!.GetValue<decimal>()).ShouldBe([1m, 2m, 3m]);
    }

    [Fact]
    public async Task T_MI_03_an_empty_collection_completes_immediately_with_an_empty_output()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            MiService("Each", "each", sequential: false, "= items", "item", "results", "= item"),
            End(),
            Flow("f1", "Start", "Each"),
            Flow("f2", "Each", "End")));

        var key = await engine.StartAsync("p", Json("{\"items\":[]}"));

        engine.AssertCompleted(key);
        engine.Variables(key)["results"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task FR_EXEC_012_multi_instance_input_must_be_a_list_boundaries_cover_the_whole_body_and_cancel_ends_all()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            MiService("Each", "each", sequential: false, "= items", "item"),
            BoundaryTimer("Late", "Each", "PT1H"),
            End("E1"),
            End("E2"),
            Flow("f1", "Start", "Each"),
            Flow("f2", "Each", "E1"),
            Flow("f3", "Late", "E2")));
        var bad = await engine.StartAsync("p", Json("{\"items\":5}"));
        var good = await engine.StartAsync("p", Json("{\"items\":[1,2]}"));

        engine.OpenIncidents(bad).ShouldHaveSingleItem().ErrorType.ShouldBe("EXTRACT_VALUE_ERROR");
        engine.AdvanceTime(TimeSpan.FromHours(1));
        await engine.RunTimersAsync();

        engine.AssertCompleted(good);
        engine.State(good).Jobs.Values.ShouldAllBe(j => j.State == JobState.Canceled);
        engine.State(good).Jobs.Count.ShouldBe(2);
    }

    private static string MessageProcess(bool interrupting) => ProcessWithMessages(
        "p",
        MessageDef("M1", "cancel-order", "= id"),
        Start(),
        Service("Work", "work"),
        End("Done"),
        EventSubProcess("Esp", EspStartMessage("EspStart", "M1", interrupting), Service("Compensate", "compensate"), End("EspEnd"), Flow("e1", "EspStart", "Compensate"), Flow("e2", "Compensate", "EspEnd")),
        Flow("f1", "Start", "Work"),
        Flow("f2", "Work", "Done"));

    [Fact]
    public async Task T_ESP_01_a_non_interrupting_event_sub_process_runs_while_the_main_flow_continues()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(MessageProcess(interrupting: false));
        var key = await engine.StartAsync("p", Json("{\"id\":\"o-1\"}"));

        TestEngine.Ok(await engine.PublishAsync("cancel-order", "o-1"));

        engine.State(key).LiveElements().Select(e => e.ElementId).ShouldBe(["Work", "Esp", "Compensate"], ignoreOrder: true);
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("work"))!.Key));
        engine.AssertActiveElements(key, "Esp", "Compensate");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("compensate"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task T_ESP_01_an_interrupting_event_sub_process_terminates_the_rest_of_the_scope()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(MessageProcess(interrupting: true));
        var key = await engine.StartAsync("p", Json("{\"id\":\"o-2\"}"));
        var work = (await engine.ActivateJobAsync("work"))!;

        TestEngine.Ok(await engine.PublishAsync("cancel-order", "o-2"));

        engine.State(key).Jobs[work.Key].State.ShouldBe(JobState.Canceled);
        engine.AssertActiveElements(key, "Esp", "Compensate");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("compensate"))!.Key));
        engine.AssertCompleted(key);
        engine.HistoryTypes(key, "Done").ShouldNotContain("ELEMENT_COMPLETED");
    }

    [Fact]
    public async Task T_ESP_01_timer_and_error_event_sub_processes_inside_a_sub_process()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "p",
            ErrorDef("E1", "BOOM"),
            Start(),
            SubProcess(
                "Sub",
                string.Empty,
                Start("SS"),
                Service("Inner", "inner"),
                End("SE"),
                EventSubProcess("OnError", EspStartError("ES", "E1"), Service("Fix", "fix"), End("FE"), Flow("x1", "ES", "Fix"), Flow("x2", "Fix", "FE")),
                Flow("s1", "SS", "Inner"),
                Flow("s2", "Inner", "SE")),
            End(),
            Flow("f1", "Start", "Sub"),
            Flow("f2", "Sub", "End")));
        var key = await engine.StartAsync("p");

        TestEngine.Ok(await engine.ThrowErrorAsync((await engine.ActivateJobAsync("inner"))!.Key, "BOOM"));

        engine.AssertActiveElements(key, "Sub", "OnError", "Fix");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("fix"))!.Key));
        engine.AssertCompleted(key);

        var timed = new TestEngine();
        await timed.DeployAsync(Process(
            "t",
            Start(),
            Service("Work", "work"),
            End(),
            EventSubProcess("Late", EspStartTimer("TS", "PT1H", interrupting: true), Manual("Escalate"), End("TE"), Flow("t1", "TS", "Escalate"), Flow("t2", "Escalate", "TE")),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "End")));
        var waiting = await timed.StartAsync("t");
        timed.AdvanceTime(TimeSpan.FromHours(1));
        await timed.RunTimersAsync();

        timed.AssertCompleted(waiting);
        timed.State(waiting).Jobs.Values.Single().State.ShouldBe(JobState.Canceled);
    }

    [Fact]
    public async Task FR_DEP_004_multi_instance_call_activity_and_event_sub_process_definitions_are_validated()
    {
        var engine = new TestEngine();

        var bad = await engine.TryDeployAsync(Process(
            "p",
            Start(),
            MiService("Each", "each", sequential: false, string.Empty, string.Empty),
            "<bpmn:callActivity id=\"Call\"/>",
            EventSubProcess("Esp", "<bpmn:startEvent id=\"NoTrigger\"/>", End("EE")),
            End(),
            Flow("f1", "Start", "Each"),
            Flow("f2", "Each", "Call"),
            Flow("f3", "Call", "End")));

        var text = TestEngine.Describe(bad is Engine.Results.Failure f ? f : throw new InvalidOperationException("expected failure"));
        text.ShouldContain("INVALID_MULTI_INSTANCE");
        text.ShouldContain("INVALID_CALL_ACTIVITY");
        text.ShouldContain("INVALID_EVENT_SUBPROCESS");
    }
}
