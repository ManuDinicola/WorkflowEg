using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using Flowd.Engine.Effects;
using Flowd.Engine.Execution;
using Flowd.Engine.State;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>T-EXEC-01..12 and the variable rules of section 8 (conformance on the in-memory engine).</summary>
public sealed class ExecutionTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public async Task T_EXEC_01_start_to_end_completes_with_ordered_lifecycle_history()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), End(), Flow("f1", "Start", "End")));

        var key = await engine.StartAsync("p");

        engine.AssertCompleted(key);
        var lifecycle = new[] { "ELEMENT_ACTIVATING", "ELEMENT_ACTIVE", "ELEMENT_COMPLETING", "ELEMENT_COMPLETED" };
        engine.HistoryTypes(key, "Start").ShouldBe(lifecycle);
        engine.HistoryTypes(key, "End").ShouldBe(lifecycle);
        var all = engine.HistoryTypes(key);
        all.First().ShouldBe("INSTANCE_STARTED");
        all.Last().ShouldBe("INSTANCE_COMPLETED");
    }

    [Fact]
    public async Task T_EXEC_02_service_task_creates_a_job_and_completing_it_finishes_the_instance()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), Service("A", "work"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "A");
        var job = (await engine.ActivateJobAsync("work")).ShouldNotBeNull();
        job.InstanceKey.ShouldBe(key);
        job.Retries.ShouldBe(3);
        TestEngine.Ok(await engine.CompleteJobAsync(job.Key));

        engine.AssertCompleted(key);
    }

    private static string ExclusiveProcess(string x, bool withDefault) => Process(
        "p",
        Start(),
        Exclusive("G", withDefault ? "fd" : null),
        Service("A", "a"),
        Service("B", "b"),
        Service("D", "d"),
        End(),
        Flow("f0", "Start", "G"),
        Flow("fa", "G", "A", $"= {x} > 5"),
        Flow("fb", "G", "B", $"= {x} > 1"),
        Flow("fd", "G", "D"),
        Flow("fe1", "A", "End"),
        Flow("fe2", "B", "End"),
        Flow("fe3", "D", "End"));

    [Fact]
    public async Task T_EXEC_03_exclusive_gateway_takes_the_first_true_condition_in_document_order()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ExclusiveProcess("x", withDefault: true));
        var key = await engine.StartAsync("p", Json("{\"x\":10}"));

        engine.AssertActiveElements(key, "A");
        (await engine.ActivateJobAsync("b")).ShouldBeNull();
    }

    [Fact]
    public async Task T_EXEC_04_exclusive_gateway_without_true_condition_takes_the_default_flow()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ExclusiveProcess("x", withDefault: true));
        var key = await engine.StartAsync("p", Json("{\"x\":0}"));

        engine.AssertActiveElements(key, "D");
    }

    [Fact]
    public async Task T_EXEC_05_exclusive_gateway_without_match_or_default_raises_an_incident_and_other_branches_continue()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Parallel("Fork"),
            Exclusive("G"),
            Service("Other", "other"),
            Service("A", "a"),
            Service("B", "b"),
            End("E1"),
            End("E2"),
            Flow("f0", "Start", "Fork"),
            Flow("f1", "Fork", "G"),
            Flow("f2", "Fork", "Other"),
            Flow("fa", "G", "A", "= x > 5"),
            Flow("fb", "G", "B", "= x > 6"),
            Flow("fo", "Other", "E1"),
            Flow("fa2", "A", "E2"),
            Flow("fb2", "B", "E2")));
        var key = await engine.StartAsync("p", Json("{\"x\":0}"));

        engine.OpenIncidents(key).ShouldHaveSingleItem().ErrorType.ShouldBe("CONDITION_ERROR");
        var other = (await engine.ActivateJobAsync("other")).ShouldNotBeNull();
        TestEngine.Ok(await engine.CompleteJobAsync(other.Key));

        engine.State(key).State.ShouldBe(InstanceState.Active);
        engine.AssertActiveElements(key, "G");
    }

    private static string ForkJoin() => Process(
        "p",
        Start(),
        Parallel("Fork"),
        Service("A", "a"),
        Service("B", "b"),
        Parallel("Join"),
        Service("C", "c"),
        End(),
        Flow("f0", "Start", "Fork"),
        Flow("f1", "Fork", "A"),
        Flow("f2", "Fork", "B"),
        Flow("f3", "A", "Join"),
        Flow("f4", "B", "Join"),
        Flow("f5", "Join", "C"),
        Flow("f6", "C", "End"));

    [Fact]
    public async Task T_EXEC_06_parallel_fork_and_join_continue_exactly_once()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ForkJoin());
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "A", "B");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("a"))!.Key));
        engine.AssertActiveElements(key, "B", "Join");
        (await engine.ActivateJobAsync("c")).ShouldBeNull();

        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("b"))!.Key));

        engine.AssertActiveElements(key, "C");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("c"))!.Key));
        engine.AssertCompleted(key);
        engine.HistoryTypes(key, "C").Count(t => t == "ELEMENT_COMPLETED").ShouldBe(1);
    }

    [Fact]
    public async Task T_EXEC_07_extra_token_on_a_join_flow_waits_and_does_not_double_fire()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Parallel("Fork"),
            Manual("M"),
            Service("B", "b"),
            Parallel("Join"),
            End(),
            Flow("f0", "Start", "Fork"),
            Flow("f1", "Fork", "M"),
            Flow("f2", "Fork", "M"),
            Flow("f3", "Fork", "B"),
            Flow("f4", "M", "Join"),
            Flow("f5", "B", "Join"),
            Flow("f6", "Join", "End")));
        var key = await engine.StartAsync("p");

        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("b"))!.Key));

        engine.HistoryTypes(key, "End").Count(t => t == "ELEMENT_COMPLETED").ShouldBe(1);
        engine.HistoryTypes(key).ShouldContain("JOIN_TOKENS_ABANDONED");
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task T_EXEC_08_terminate_end_event_kills_sibling_branches_and_completes_the_instance()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Parallel("Fork"),
            Service("Slow", "slow"),
            TerminateEnd("Kill"),
            End(),
            Flow("f0", "Start", "Fork"),
            Flow("f1", "Fork", "Slow"),
            Flow("f2", "Fork", "Kill"),
            Flow("f3", "Slow", "End")));

        var key = await engine.StartAsync("p");

        engine.AssertCompleted(key);
        engine.State(key).Jobs.Values.ShouldHaveSingleItem().State.ShouldBe(JobState.Canceled);
        engine.State(key).LiveElements().ShouldBeEmpty();
    }

    [Fact]
    public async Task T_EXEC_09_cancelling_an_instance_cancels_its_jobs_and_terminates_it()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), Service("A", "work"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        var key = await engine.StartAsync("p");

        TestEngine.Ok(await engine.CancelAsync(key));

        engine.AssertTerminated(key);
        engine.State(key).Jobs.Values.ShouldHaveSingleItem().State.ShouldBe(JobState.Canceled);
        TestEngine.ConflictCode(await engine.CancelAsync(key)).ShouldBe("INSTANCE_NOT_ACTIVE");
    }

    [Fact]
    public async Task T_EXEC_10_output_mapping_writes_to_the_parent_scope_and_input_variables_stay_local()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Service("Charge", "charge", inner: Mapping(Input("= order.total", "amount"), Output("= receipt", "receiptId"))),
            End(),
            Flow("f1", "Start", "Charge"),
            Flow("f2", "Charge", "End")));
        var key = await engine.StartAsync("p", Json("{\"order\":{\"total\":42}}"));

        var job = (await engine.ActivateJobAsync("charge")).ShouldNotBeNull();
        job.Variables["amount"]!.GetValue<decimal>().ShouldBe(42);
        TestEngine.Ok(await engine.CompleteJobAsync(job.Key, Json("{\"receipt\":\"R-1\",\"ignored\":true}")));

        var variables = engine.Variables(key);
        variables["receiptId"]!.GetValue<string>().ShouldBe("R-1");
        variables.ContainsKey("amount").ShouldBeFalse();
        variables.ContainsKey("ignored").ShouldBeFalse();
    }

    [Fact]
    public async Task FR_VAR_without_output_mapping_all_result_variables_propagate_unless_disabled()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("all", Start(), Service("A", "t1"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        await engine.DeployAsync(Process("none", Start(), Service("A", "t2", inner: Mapping(string.Empty, string.Empty, propagateAll: false)), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        var all = await engine.StartAsync("all");
        var none = await engine.StartAsync("none");

        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("t1"))!.Key, Json("{\"r\":1}")));
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("t2"))!.Key, Json("{\"r\":1}")));

        engine.Variables(all).ContainsKey("r").ShouldBeTrue();
        engine.Variables(none).ContainsKey("r").ShouldBeFalse();
    }

    [Fact]
    public async Task T_EXEC_11_the_same_command_sequence_always_produces_the_same_history()
    {
        var baseline = await RunScenarioAsync(seed: 7);

        for (var i = 0; i < 100; i++)
        {
            (await RunScenarioAsync(seed: 7)).ShouldBe(baseline);
        }
    }

    private static async Task<string> RunScenarioAsync(int seed)
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ForkJoin());
        var key = await engine.StartAsync("p", Json("{\"n\":1}"));
        var order = new[] { "a", "b" }.OrderBy(_ => new Random(seed).Next()).ToArray();
        foreach (var type in order)
        {
            TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync(type))!.Key));
        }

        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("c"))!.Key));
        return string.Join("|", engine.Store.HistoryOf(key).Select(h => $"{h.EventType}:{h.ElementId}"));
    }

    [Fact]
    public async Task T_EXEC_12_a_cycle_hits_the_loop_limit_as_an_incident_and_the_engine_stays_responsive()
    {
        var engine = new TestEngine(new EngineOptions { MaxActivationsPerCommand = 50 });
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Manual("A"),
            Manual("B"),
            Flow("f0", "Start", "A"),
            Flow("f1", "A", "B"),
            Flow("f2", "B", "A")));

        var key = await engine.StartAsync("p");

        engine.OpenIncidents(key).ShouldHaveSingleItem().ErrorType.ShouldBe("LOOP_LIMIT_EXCEEDED");
        engine.State(key).State.ShouldBe(InstanceState.Active);
        TestEngine.Ok(await engine.CancelAsync(key));
        engine.AssertTerminated(key);
    }

    [Fact]
    public async Task FR_VAR_001_writes_go_to_the_scope_that_defines_the_variable()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), Service("A", "work", inner: Mapping(Input("= 1", "local"), string.Empty)), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        var key = await engine.StartAsync("p", Json("{\"shared\":1}"));
        var job = (await engine.ActivateJobAsync("work"))!;
        var element = engine.State(key).LiveElements().Single();

        TestEngine.Ok(await engine.SetVariablesAsync(key, Json("{\"shared\":2,\"fresh\":3}")));
        engine.State(key).Variables.InScope(key)["shared"]!.GetValue<int>().ShouldBe(2);
        TestEngine.Ok(await engine.SetVariablesAsync(key, Json("{\"scoped\":9}"), local: true));

        engine.State(key).Variables.InScope(key).ContainsKey("scoped").ShouldBeTrue();
        element.Key.ShouldNotBe(key);
        job.Variables.ContainsKey("local").ShouldBeTrue();
    }

    [Fact]
    public async Task FR_VAR_003_reserved_variable_names_are_rejected()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), Service("A", "work"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));

        var start = await engine.TryStartAsync("p", Json("{\"_flowdSecret\":1}"));

        TestEngine.Describe((start is Engine.Results.Failure f ? f : throw new InvalidOperationException("expected failure"))).ShouldContain("_flowd");
    }

    [Fact]
    public async Task FR_VAR_002_oversized_variables_are_rejected()
    {
        var engine = new TestEngine(new EngineOptions { MaxVariableBytes = 50 });
        await engine.DeployAsync(Process("p", Start(), Service("A", "work"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));

        var start = await engine.TryStartAsync("p", Json($"{{\"big\":\"{new string('x', 100)}\"}}"));

        (start is Engine.Results.Failure).ShouldBeTrue();
    }

    [Fact]
    public async Task T_INC_01_missing_variable_raises_extract_value_error_and_resolving_after_fixing_proceeds()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), Service("A", "= jobType"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));

        var key = await engine.StartAsync("p");

        var incident = engine.OpenIncidents(key).ShouldHaveSingleItem();
        incident.ErrorType.ShouldBe("EXTRACT_VALUE_ERROR");
        TestEngine.Ok(await engine.SetVariablesAsync(key, Json("{\"jobType\":\"dynamic\"}")));
        TestEngine.Ok(await engine.ResolveIncidentAsync(incident.Key));

        var job = (await engine.ActivateJobAsync("dynamic")).ShouldNotBeNull();
        TestEngine.Ok(await engine.CompleteJobAsync(job.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task T_DEP_03_a_running_old_version_finishes_on_its_own_version_while_new_starts_use_the_latest()
    {
        var engine = new TestEngine();
        var v1 = await engine.DeployAsync(Process("p", Start(), Service("A", "old"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        var running = await engine.StartAsync("p");
        var v2 = await engine.DeployAsync(Process("p", Start(), Service("A", "new"), End(), Flow("f1", "Start", "A"), Flow("f2", "A", "End")));
        var fresh = await engine.StartAsync("p");

        v2.Version.ShouldBe(2);
        engine.State(running).DefinitionKey.ShouldBe(v1.Key);
        engine.State(fresh).DefinitionKey.ShouldBe(v2.Key);
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("old"))!.Key));
        engine.AssertCompleted(running);
        (await engine.ActivateJobAsync("new")).ShouldNotBeNull();
    }
}
