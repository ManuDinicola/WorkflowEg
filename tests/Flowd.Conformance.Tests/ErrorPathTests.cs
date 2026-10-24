using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using Flowd.Engine.Commands;
using Flowd.Engine.Execution;
using Flowd.Engine.State;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>Failure and rejection paths: incidents, refused commands, invalid input (CC-008).</summary>
public sealed class ErrorPathTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static async Task<(TestEngine Engine, long Key)> StartTaskAsync(string? retries = null)
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), Service("A", "work", retries), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        return (engine, await engine.StartAsync("p"));
    }

    [Fact]
    public async Task Exclusive_gateway_conditions_that_are_not_boolean_or_fail_raise_condition_errors()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("num", Start(), Exclusive("G", "d"), End("E1"), End("E2"), Flow("f0", "Start", "G"), Flow("a", "G", "E1", "= 5"), Flow("d", "G", "E2")));
        await engine.DeployAsync(Process("undef", Start(), Exclusive("G", "d"), End("E1"), End("E2"), Flow("f0", "Start", "G"), Flow("a", "G", "E1", "= nothing > 1"), Flow("d", "G", "E2")));

        var notBoolean = await engine.StartAsync("num");
        var failed = await engine.StartAsync("undef");

        engine.OpenIncidents(notBoolean).Single().ErrorMessage.ShouldContain("boolean");
        engine.OpenIncidents(failed).Single().ErrorType.ShouldBe("CONDITION_ERROR");
    }

    [Fact]
    public async Task Invalid_job_type_and_retries_expressions_raise_extract_value_errors()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("a", Start(), Service("A", "= 5"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        await engine.DeployAsync(Process("b", Start(), Service("A", "work", "= \"many\""), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        await engine.DeployAsync(Process("c", Start(), Service("A", "work", "-1"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));

        foreach (var id in new[] { "a", "b", "c" })
        {
            var key = await engine.StartAsync(id);
            engine.OpenIncidents(key).ShouldHaveSingleItem().ErrorType.ShouldBe("EXTRACT_VALUE_ERROR");
        }
    }

    [Fact]
    public async Task Failing_input_and_output_mappings_raise_io_mapping_errors_and_outputs_can_be_retried()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("in", Start(), Service("A", "work", inner: Mapping(Input("= missing", "x"), string.Empty)), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        await engine.DeployAsync(Process("out", Start(), Service("A", "work", inner: Mapping(string.Empty, Output("= result.id", "id"))), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        var input = await engine.StartAsync("in");
        var output = await engine.StartAsync("out");

        engine.OpenIncidents(input).Single().ErrorType.ShouldBe("IO_MAPPING_ERROR");
        var job = (await engine.ActivateJobAsync("work"))!;
        TestEngine.Ok(await engine.CompleteJobAsync(job.Key, Json("{}")));
        var incident = engine.OpenIncidents(output).Single();
        incident.ErrorType.ShouldBe("IO_MAPPING_ERROR");

        TestEngine.Ok(await engine.ResolveIncidentAsync(incident.Key));
        engine.OpenIncidents(output).Count.ShouldBe(1);
        TestEngine.ConflictCode(await engine.ResolveIncidentAsync(incident.Key)).ShouldBe("INCIDENT_NOT_OPEN");
    }

    [Fact]
    public async Task Reserved_names_and_oversized_values_in_mappings_raise_incidents()
    {
        var engine = new TestEngine(new EngineOptions { MaxVariableBytes = 20 });
        await engine.DeployAsync(Process("big", Start(), Service("A", "work", inner: Mapping(Input("= \"this text is clearly longer than twenty bytes\"", "x"), string.Empty)), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));

        var key = await engine.StartAsync("big");

        engine.OpenIncidents(key).Single().ErrorType.ShouldBe("VARIABLE_SIZE_EXCEEDED");
    }

    [Fact]
    public async Task Job_commands_reject_bad_input_and_unknown_jobs()
    {
        var (engine, key) = await StartTaskAsync();
        var job = (await engine.ActivateJobAsync("work"))!;

        (TestEngine.ConflictCode(await engine.FailJobAsync(job.Key, -1)) is null).ShouldBeTrue();
        TestEngine.Failed(await engine.FailJobAsync(job.Key, -1)).ShouldBeTrue();
        TestEngine.Failed(await engine.CompleteJobAsync(job.Key, Json("{\"_flowdX\":1}"))).ShouldBeTrue();
        TestEngine.Failed(await engine.UpdateRetriesAsync(job.Key, -1)).ShouldBeTrue();
        TestEngine.Failed(await engine.ExecuteAsync(key, new CompleteJob(999, null, null))).ShouldBeTrue();
        TestEngine.Failed(await engine.ExecuteAsync(key, new ResolveIncident(999))).ShouldBeTrue();
        TestEngine.Failed(await engine.ExecuteAsync(123456, new CancelInstance())).ShouldBeTrue();
        TestEngine.Ok(await engine.CompleteJobAsync(job.Key));
        TestEngine.Failed(await engine.UpdateRetriesAsync(job.Key, 2)).ShouldBeTrue();
        TestEngine.Failed(await engine.FailJobAsync(job.Key, 1)).ShouldBeTrue();
        TestEngine.Failed(await engine.ThrowErrorAsync(job.Key, "X")).ShouldBeTrue();
    }

    [Fact]
    public async Task Set_variables_rejects_unknown_scopes_and_terminated_instances()
    {
        var (engine, key) = await StartTaskAsync();

        TestEngine.Failed(await engine.ExecuteAsync(key, new SetVariables(Json("{\"a\":1}"), 424242, false))).ShouldBeTrue();
        TestEngine.Ok(await engine.CancelAsync(key));
        TestEngine.ConflictCode(await engine.SetVariablesAsync(key, Json("{\"a\":1}"))).ShouldBe("INSTANCE_NOT_ACTIVE");
        TestEngine.Failed(await engine.ExecuteAsync(key, new TriggerTimer(1))).ShouldBeTrue();
        TestEngine.Failed(await engine.ExecuteAsync(key, new CorrelateMessage(1, 1, "m", null))).ShouldBeTrue();
    }

    [Fact]
    public async Task Starting_with_a_wrong_start_element_or_twice_is_rejected()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("two", Start("S1"), Start("S2"), End(), Flow("f1", "S1", "End"), Flow("f2", "S2", "End")));

        TestEngine.Failed(await engine.TryStartAsync("two")).ShouldBeTrue();
        TestEngine.Failed(await engine.TryStartAsync("two", null, "End")).ShouldBeTrue();
        var started = TestEngine.Ok(await engine.TryStartAsync("two", null, "S2"));
        TestEngine.ConflictCode(await engine.ExecuteAsync(started.InstanceKey, new StartInstance(null, null))).ShouldBe("INSTANCE_NOT_ACTIVE");
    }

    [Fact]
    public async Task Timer_and_correlation_failures_raise_incidents_and_stale_triggers_are_refused()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("t", Start(), CatchTimer("T", "= 7"), End(), Flow("f1", "Start", "T"), Flow("f2", "T", "End")));
        await engine.DeployAsync(ProcessWithMessages("m", MessageDef("M1", "x", "= 5"), Start(), CatchMessage("W", "M1"), End(), Flow("f1", "Start", "W"), Flow("f2", "W", "End")));
        await engine.DeployAsync(Process("ok", Start(), CatchTimer("T", "PT1H"), End(), Flow("f1", "Start", "T"), Flow("f2", "T", "End")));

        var badTimer = await engine.StartAsync("t");
        var badKey = await engine.StartAsync("m");
        engine.OpenIncidents(badTimer).Single().ErrorType.ShouldBe("EXTRACT_VALUE_ERROR");
        engine.OpenIncidents(badKey).Single().ErrorType.ShouldBe("EXTRACT_VALUE_ERROR");

        var good = await engine.StartAsync("ok");
        var timer = engine.State(good).Timers.Values.Single();
        TestEngine.ConflictCode(await engine.ExecuteAsync(good, new TriggerTimer(timer.Key))).ShouldBe("TIMER_NOT_DUE");
        engine.AdvanceTime(TimeSpan.FromHours(2));
        TestEngine.Ok(await engine.ExecuteAsync(good, new TriggerTimer(timer.Key)));
        TestEngine.ConflictCode(await engine.ExecuteAsync(good, new TriggerTimer(timer.Key))).ShouldBe("INSTANCE_NOT_ACTIVE");
    }

    [Fact]
    public async Task Event_gateway_with_a_receive_task_target_and_a_failing_branch_cleans_up()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "g",
            MessageDef("M1", "reply", "= id"),
            Start(),
            EventGateway("G"),
            ReceiveTask("R", "M1"),
            CatchTimer("T", "PT1H"),
            End("E1"),
            End("E2"),
            Flow("f0", "Start", "G"),
            Flow("f1", "G", "R"),
            Flow("f2", "G", "T"),
            Flow("f3", "R", "E1"),
            Flow("f4", "T", "E2")));
        var ok = await engine.StartAsync("g", Json("{\"id\":\"a\"}"));
        var broken = await engine.StartAsync("g");

        TestEngine.Ok(await engine.PublishAsync("reply", "a"));

        engine.AssertCompleted(ok);
        engine.OpenIncidents(broken).Single().ErrorType.ShouldBe("EXTRACT_VALUE_ERROR");
        engine.State(broken).Timers.Values.ShouldAllBe(t => !t.IsActive);
    }

    [Fact]
    public async Task Sub_process_input_and_output_failures_raise_incidents_and_script_write_errors_too()
    {
        var engine = new TestEngine(new EngineOptions { MaxVariableBytes = 16 });
        await engine.DeployAsync(Process(
            "in",
            Start(),
            SubProcess("Sub", Mapping(Input("= nothing", "x"), string.Empty), Start("SS"), End("SE"), Flow("s1", "SS", "SE")),
            End(),
            Flow("f1", "Start", "Sub"),
            Flow("f2", "Sub", "End")));
        await engine.DeployAsync(Process(
            "out",
            Start(),
            SubProcess("Sub", Mapping(string.Empty, Output("= nothing", "y")), Start("SS"), End("SE"), Flow("s1", "SS", "SE")),
            End(),
            Flow("f1", "Start", "Sub"),
            Flow("f2", "Sub", "End")));
        await engine.DeployAsync(Process("script", Start(), Script("S", "= \"a string longer than sixteen bytes\"", "big"), End(), Flow("f1", "Start", "S"), Flow("f2", "S", "End")));

        var input = await engine.StartAsync("in");
        var output = await engine.StartAsync("out");
        var script = await engine.StartAsync("script");

        engine.OpenIncidents(input).Single().ErrorType.ShouldBe("IO_MAPPING_ERROR");
        engine.OpenIncidents(output).Single().ErrorType.ShouldBe("IO_MAPPING_ERROR");
        engine.OpenIncidents(script).Single().ErrorType.ShouldBe("VARIABLE_SIZE_EXCEEDED");
        engine.State(output).LiveElements().Select(e => e.ElementId).ShouldContain("Sub");
        engine.State(input).State.ShouldBe(InstanceState.Active);
    }
}
