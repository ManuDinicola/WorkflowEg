using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using Flowd.Engine.State;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>M5: BPMN errors (T-ERR-01..03), signals (T-SIG-01), throw events and user tasks (T-UT-01).</summary>
public sealed class ErrorsSignalsUserTaskTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public async Task T_ERR_01_a_worker_error_is_caught_by_the_matching_boundary_event()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "p",
            ErrorDef("E1", "PAYMENT_DECLINED") + ErrorDef("E2", "OTHER"),
            Start(),
            Service("Pay", "pay"),
            BoundaryError("Declined", "Pay", "E1"),
            Service("Notify", "notify"),
            End("Ok"),
            End("Failed"),
            Flow("f1", "Start", "Pay"),
            Flow("f2", "Pay", "Ok"),
            Flow("f3", "Declined", "Notify"),
            Flow("f4", "Notify", "Failed")));
        var key = await engine.StartAsync("p");
        var job = (await engine.ActivateJobAsync("pay"))!;

        TestEngine.Ok(await engine.ThrowErrorAsync(job.Key, "PAYMENT_DECLINED"));

        engine.AssertActiveElements(key, "Notify");
        engine.State(key).Jobs[job.Key].State.ShouldBe(JobState.ErrorThrown);
        engine.OpenIncidents(key).ShouldBeEmpty();
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("notify"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task T_ERR_01_a_catch_all_boundary_handles_any_code_and_the_specific_one_wins_when_both_exist()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "p",
            ErrorDef("E1", "KNOWN"),
            Start(),
            Service("Work", "work"),
            BoundaryError("Any", "Work"),
            BoundaryError("Specific", "Work", "E1"),
            End("Done"),
            End("AnyEnd"),
            End("SpecificEnd"),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "Done"),
            Flow("f3", "Any", "AnyEnd"),
            Flow("f4", "Specific", "SpecificEnd")));
        var known = await engine.StartAsync("p");
        var unknown = await engine.StartAsync("p");

        TestEngine.Ok(await engine.ThrowErrorAsync((await engine.ActivateJobAsync("work"))!.Key, "KNOWN"));
        TestEngine.Ok(await engine.ThrowErrorAsync((await engine.ActivateJobAsync("work"))!.Key, "WHATEVER"));

        engine.HistoryTypes(known, "SpecificEnd").ShouldContain("ELEMENT_COMPLETED");
        engine.HistoryTypes(unknown, "AnyEnd").ShouldContain("ELEMENT_COMPLETED");
    }

    [Fact]
    public async Task T_ERR_02_an_error_inside_a_sub_process_is_caught_by_the_sub_process_boundary()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "p",
            ErrorDef("E1", "BOOM"),
            Start(),
            SubProcess("Sub", string.Empty, Start("SS"), Service("Inner", "inner"), End("SE"), Flow("s1", "SS", "Inner"), Flow("s2", "Inner", "SE")),
            BoundaryError("Caught", "Sub", "E1"),
            Service("Recover", "recover"),
            End("Done"),
            Flow("f1", "Start", "Sub"),
            Flow("f2", "Sub", "Done"),
            Flow("f3", "Caught", "Recover"),
            Flow("f4", "Recover", "Done")));
        var key = await engine.StartAsync("p");

        TestEngine.Ok(await engine.ThrowErrorAsync((await engine.ActivateJobAsync("inner"))!.Key, "BOOM"));

        engine.AssertActiveElements(key, "Recover");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("recover"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task T_ERR_02_an_error_end_event_inside_a_sub_process_propagates_and_siblings_are_terminated()
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
                Parallel("Fork"),
                ErrorEnd("Fail", "E1"),
                Service("Slow", "slow"),
                End("SE"),
                Flow("s1", "SS", "Fork"),
                Flow("s2", "Fork", "Fail"),
                Flow("s3", "Fork", "Slow"),
                Flow("s4", "Slow", "SE")),
            BoundaryError("Caught", "Sub", "E1"),
            End("Done"),
            End("Handled"),
            Flow("f1", "Start", "Sub"),
            Flow("f2", "Sub", "Done"),
            Flow("f3", "Caught", "Handled")));

        var key = await engine.StartAsync("p");

        engine.AssertCompleted(key);
        engine.HistoryTypes(key, "Handled").ShouldContain("ELEMENT_COMPLETED");
        engine.State(key).Jobs.Values.ShouldBeEmpty(); // the queued sibling step never ran: its scope was already terminated
    }

    [Fact]
    public async Task T_ERR_03_an_error_without_a_handler_raises_unhandled_error_event()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "p",
            ErrorDef("E1", "OTHER"),
            Start(),
            Service("Work", "work"),
            BoundaryError("Caught", "Work", "E1"),
            End("Done"),
            End("Handled"),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "Done"),
            Flow("f3", "Caught", "Handled")));
        await engine.DeployAsync(ProcessWithMessages("end", ErrorDef("E9", "NOPE"), Start(), ErrorEnd("Boom", "E9"), Flow("f1", "Start", "Boom")));
        var key = await engine.StartAsync("p");
        var viaEnd = await engine.StartAsync("end");

        TestEngine.Ok(await engine.ThrowErrorAsync((await engine.ActivateJobAsync("work"))!.Key, "PAYMENT_DECLINED"));

        engine.OpenIncidents(key).ShouldHaveSingleItem().ErrorType.ShouldBe("UNHANDLED_ERROR_EVENT");
        engine.OpenIncidents(viaEnd).ShouldHaveSingleItem().ErrorType.ShouldBe("UNHANDLED_ERROR_EVENT");
    }

    [Fact]
    public async Task T_SIG_01_a_broadcast_signal_wakes_every_waiting_instance_and_is_never_buffered()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages("p", SignalDef("S1", "go"), Start(), CatchSignal("Wait", "S1"), End(), Flow("f1", "Start", "Wait"), Flow("f2", "Wait", "End")));
        await engine.DeployAsync(ProcessWithMessages(
            "b",
            SignalDef("S1", "go"),
            Start(),
            Service("Work", "work"),
            BoundarySignal("Stop", "Work", "S1"),
            End("E1"),
            End("E2"),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "E1"),
            Flow("f3", "Stop", "E2")));
        (await engine.BroadcastAsync("go")).ShouldBe(0);
        var waiting = new[] { await engine.StartAsync("p"), await engine.StartAsync("p") };
        var boundary = await engine.StartAsync("b");

        (await engine.BroadcastAsync("go", Json("{\"from\":\"ops\"}"))).ShouldBe(3);

        waiting.ShouldAllBe(k => engine.State(k).State == InstanceState.Completed);
        engine.AssertCompleted(boundary);
        engine.Variables(waiting[0])["from"]!.GetValue<string>().ShouldBe("ops");
        var late = await engine.StartAsync("p");
        engine.State(late).State.ShouldBe(InstanceState.Active);
    }

    [Fact]
    public async Task FR_SIG_001_signal_start_events_and_signal_throw_events_work_together()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages("listener", SignalDef("S1", "alert"), StartSignal("S", "S1"), Service("A", "handle"), End(), Flow("f1", "S", "A"), Flow("f2", "A", "End")));
        await engine.DeployAsync(ProcessWithMessages("thrower", SignalDef("S1", "alert"), Start(), ThrowSignal("Throw", "S1"), End(), Flow("f1", "Start", "Throw"), Flow("f2", "Throw", "End")));
        await engine.DeployAsync(ProcessWithMessages("waiter", SignalDef("S1", "alert"), Start(), CatchSignal("Wait", "S1"), End(), Flow("f1", "Start", "Wait"), Flow("f2", "Wait", "End")));
        var waiter = await engine.StartAsync("waiter");

        var thrower = await engine.StartAsync("thrower");

        engine.AssertCompleted(thrower);
        engine.AssertCompleted(waiter);
        (await engine.ActivateJobAsync("handle")).ShouldNotBeNull();
    }

    [Fact]
    public async Task FR_EXEC_throw_events_none_passes_through_and_message_throw_creates_a_job()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), ThrowNone("T1"), ThrowMessage("T2", "send-mail"), End(), Flow("f1", "Start", "T1"), Flow("f2", "T1", "T2"), Flow("f3", "T2", "End")));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "T2");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("send-mail"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_EXEC_008_a_message_end_event_waits_for_its_job()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), "<bpmn:endEvent id=\"End\"><bpmn:messageEventDefinition/><bpmn:extensionElements><flowd:taskDefinition type=\"final\"/></bpmn:extensionElements></bpmn:endEvent>", Flow("f1", "Start", "End")));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "End");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("final"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task T_UT_01_a_user_task_is_claimed_completed_with_variables_and_a_second_claimant_conflicts()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            UserTask("Review", "candidateGroups=\"managers, finance\"", "<flowd:formDefinition formKey=\"review-form\"/>"),
            End(),
            Flow("f1", "Start", "Review"),
            Flow("f2", "Review", "End")));
        var key = await engine.StartAsync("p");

        var task = engine.UserTasks(key).ShouldHaveSingleItem();
        task.CandidateGroups.ShouldBe(["managers", "finance"]);
        task.FormKey.ShouldBe("review-form");
        TestEngine.Ok(await engine.ClaimTaskAsync(task.Key, "alice"));
        TestEngine.ConflictCode(await engine.ClaimTaskAsync(task.Key, "bob")).ShouldBe("USER_TASK_ALREADY_CLAIMED");
        TestEngine.ConflictCode(await engine.CompleteTaskAsync(task.Key, null, "bob")).ShouldBe("USER_TASK_ASSIGNED_TO_OTHER");

        TestEngine.Ok(await engine.CompleteTaskAsync(task.Key, Json("{\"approved\":true}"), "alice"));

        engine.AssertCompleted(key);
        engine.Variables(key)["approved"]!.GetValue<bool>().ShouldBeTrue();
        TestEngine.ConflictCode(await engine.CompleteTaskAsync(task.Key)).ShouldBe("USER_TASK_NOT_ACTIVE");
    }

    [Fact]
    public async Task FR_UT_unclaim_assign_boundary_cancel_and_unknown_tasks()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "p",
            ErrorDef("E1", "X"),
            Start(),
            UserTask("Review", "assignee=\"carol\""),
            BoundaryTimer("Late", "Review", "PT1H"),
            End("Done"),
            End("Escalated"),
            Flow("f1", "Start", "Review"),
            Flow("f2", "Review", "Done"),
            Flow("f3", "Late", "Escalated")));
        var key = await engine.StartAsync("p");
        var task = engine.UserTasks(key).Single();

        task.Assignee.ShouldBe("carol");
        TestEngine.Ok(await engine.UnclaimTaskAsync(task.Key));
        TestEngine.Ok(await engine.AssignTaskAsync(task.Key, "dave"));
        engine.UserTasks(key).Single().Assignee.ShouldBe("dave");
        TestEngine.Failed(await engine.ClaimTaskAsync(999, "x")).ShouldBeTrue();

        engine.AdvanceTime(TimeSpan.FromHours(1));
        await engine.RunTimersAsync();

        engine.AssertCompleted(key);
        engine.UserTasks(key).Single().State.ShouldBe(UserTaskState.Canceled);
        TestEngine.ConflictCode(await engine.CompleteTaskAsync(task.Key)).ShouldBe("USER_TASK_NOT_ACTIVE");
    }

    [Fact]
    public async Task FR_DEP_004_error_definitions_are_only_valid_on_interrupting_boundaries_and_end_events()
    {
        var engine = new TestEngine();

        var bad = await engine.TryDeployAsync(ProcessWithMessages(
            "p",
            ErrorDef("E1", "X"),
            Start(),
            Service("Work", "work"),
            BoundaryError("Ok", "Work", "E1"),
            "<bpmn:boundaryEvent id=\"NonInt\" attachedToRef=\"Work\" cancelActivity=\"false\"><bpmn:errorEventDefinition errorRef=\"E1\"/></bpmn:boundaryEvent>",
            End(),
            Flow("f1", "Start", "Work"),
            Flow("f2", "Work", "End"),
            Flow("f3", "Ok", "End"),
            Flow("f4", "NonInt", "End")));

        TestEngine.Describe(bad is Engine.Results.Failure f ? f : throw new InvalidOperationException("expected failure")).ShouldContain("INVALID_EVENT_DEFINITION");
    }
}
