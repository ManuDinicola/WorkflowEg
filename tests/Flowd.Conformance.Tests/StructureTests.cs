using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using Flowd.Engine.State;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>M3: sub-processes, inclusive gateway, script/send/manual tasks (T-SUB-01, T-INCL-01..02).</summary>
public sealed class StructureTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public async Task T_SUB_01_sub_process_runs_its_inner_flow_and_only_mapped_variables_leave_the_scope()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            SubProcess(
                "Sub",
                Mapping(Input("= order.total", "amount"), Output("= doubled", "result")),
                Start("SubStart"),
                Service("Work", "work"),
                End("SubEnd"),
                Flow("s1", "SubStart", "Work"),
                Flow("s2", "Work", "SubEnd")),
            Service("After", "after"),
            End(),
            Flow("f1", "Start", "Sub"),
            Flow("f2", "Sub", "After"),
            Flow("f3", "After", "End")));
        var key = await engine.StartAsync("p", Json("{\"order\":{\"total\":21}}"));

        var work = (await engine.ActivateJobAsync("work")).ShouldNotBeNull();
        work.Variables["amount"]!.GetValue<decimal>().ShouldBe(21m);
        TestEngine.Ok(await engine.CompleteJobAsync(work.Key, Json("{\"doubled\":42,\"scratch\":1}")));

        engine.AssertActiveElements(key, "After");
        var variables = engine.Variables(key);
        variables["result"]!.GetValue<decimal>().ShouldBe(42m);
        variables.ContainsKey("scratch").ShouldBeFalse();
        variables.ContainsKey("amount").ShouldBeFalse();
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("after"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_EXEC_010_nested_sub_processes_complete_inside_out_and_cancel_terminates_all_levels()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            SubProcess(
                "Outer",
                string.Empty,
                Start("OS"),
                SubProcess("Inner", string.Empty, Start("IS"), Service("Deep", "deep"), End("IE"), Flow("i1", "IS", "Deep"), Flow("i2", "Deep", "IE")),
                End("OE"),
                Flow("o1", "OS", "Inner"),
                Flow("o2", "Inner", "OE")),
            End(),
            Flow("f1", "Start", "Outer"),
            Flow("f2", "Outer", "End")));
        var first = await engine.StartAsync("p");
        var second = await engine.StartAsync("p");

        engine.AssertActiveElements(first, "Outer", "Inner", "Deep");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("deep"))!.Key));
        engine.AssertCompleted(first);

        TestEngine.Ok(await engine.CancelAsync(second));
        engine.AssertTerminated(second);
        engine.State(second).LiveElements().ShouldBeEmpty();
    }

    private static string InclusiveProcess() => Process(
        "p",
        Start(),
        Inclusive("Split", "d"),
        Service("A", "a"),
        Service("B", "b"),
        Service("C", "c"),
        Inclusive("Join"),
        Service("After", "after"),
        End(),
        Flow("f0", "Start", "Split"),
        Flow("fa", "Split", "A", "= x > 0"),
        Flow("fb", "Split", "B", "= x > 1"),
        Flow("fc", "Split", "C", "= x > 5"),
        Flow("d", "Split", "A"),
        Flow("ja", "A", "Join"),
        Flow("jb", "B", "Join"),
        Flow("jc", "C", "Join"),
        Flow("fj", "Join", "After"),
        Flow("fe", "After", "End"));

    [Fact]
    public async Task T_INCL_01_two_of_three_branches_run_and_the_join_fires_once_after_both_finish()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(InclusiveProcess().Replace("<bpmn:sequenceFlow id=\"d\" sourceRef=\"Split\" targetRef=\"A\"/>", string.Empty, StringComparison.Ordinal).Replace(" default=\"d\"", string.Empty, StringComparison.Ordinal));
        var key = await engine.StartAsync("p", Json("{\"x\":3}"));

        engine.AssertActiveElements(key, "A", "B");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("a"))!.Key));
        (await engine.ActivateJobAsync("after")).ShouldBeNull();
        engine.AssertActiveElements(key, "B", "Join");

        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("b"))!.Key));

        engine.AssertActiveElements(key, "After");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("after"))!.Key));
        engine.AssertCompleted(key);
        engine.HistoryTypes(key, "After").Count(t => t == "ELEMENT_COMPLETED").ShouldBe(1);
    }

    [Fact]
    public async Task T_INCL_02_a_branch_that_was_never_taken_does_not_block_the_join()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process(
            "p",
            Start(),
            Inclusive("Split"),
            Manual("A"),
            Service("B", "b"),
            Inclusive("Join"),
            End(),
            Flow("f0", "Start", "Split"),
            Flow("fa", "Split", "A", "= x > 100"),
            Flow("fb", "Split", "B", "= x > 0"),
            Flow("ja", "A", "Join"),
            Flow("jb", "B", "Join"),
            Flow("fe", "Join", "End")));
        var key = await engine.StartAsync("p", Json("{\"x\":1}"));

        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("b"))!.Key));

        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_EXEC_005_inclusive_split_uses_the_default_flow_or_raises_an_incident()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(InclusiveProcess());
        var viaDefault = await engine.StartAsync("p", Json("{\"x\":0}"));
        engine.AssertActiveElements(viaDefault, "A");

        var noDefault = new TestEngine();
        await noDefault.DeployAsync(Process(
            "q",
            Start(),
            Inclusive("Split"),
            End("E1"),
            End("E2"),
            Flow("f0", "Start", "Split"),
            Flow("fa", "Split", "E1", "= x > 5"),
            Flow("fb", "Split", "E2", "= x > 6")));
        var stuck = await noDefault.StartAsync("q", Json("{\"x\":0}"));

        noDefault.OpenIncidents(stuck).ShouldHaveSingleItem().ErrorType.ShouldBe("CONDITION_ERROR");
    }

    [Fact]
    public async Task FR_EXEC_007_script_task_stores_the_expression_result_and_failures_raise_incidents()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("ok", Start(), Script("S", "= price * qty", "total"), End(), Flow("f1", "Start", "S"), Flow("f2", "S", "End")));
        var done = await engine.StartAsync("ok", Json("{\"price\":2.5,\"qty\":4}"));

        engine.AssertCompleted(done);
        engine.Variables(done)["total"]!.GetValue<decimal>().ShouldBe(10m);

        var bad = await engine.StartAsync("ok");
        var incident = engine.OpenIncidents(bad).ShouldHaveSingleItem();
        incident.ErrorType.ShouldBe("EXTRACT_VALUE_ERROR");
        TestEngine.Ok(await engine.SetVariablesAsync(bad, Json("{\"price\":3,\"qty\":3}")));
        TestEngine.Ok(await engine.ResolveIncidentAsync(incident.Key));
        engine.AssertCompleted(bad);
        engine.Variables(bad)["total"]!.GetValue<decimal>().ShouldBe(9m);
    }

    [Fact]
    public async Task FR_EXEC_006_send_task_behaves_like_a_service_task_and_manual_task_passes_through()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Process("p", Start(), Manual("M"), SendTask("Send", "notify"), End(), Flow("f1", "Start", "M"), Flow("f2", "M", "Send"), Flow("f3", "Send", "End")));
        var key = await engine.StartAsync("p");

        engine.AssertActiveElements(key, "Send");
        engine.State(key).LiveElements().Single().ElementType.ShouldBe("sendTask");
        TestEngine.Ok(await engine.CompleteJobAsync((await engine.ActivateJobAsync("notify"))!.Key));
        engine.AssertCompleted(key);
    }

    [Fact]
    public async Task FR_DEP_004_expressions_are_checked_for_syntax_at_deployment()
    {
        var engine = new TestEngine();

        var result = await engine.TryDeployAsync(Process(
            "p",
            Start(),
            Exclusive("G", "f2"),
            Service("A", "work", inner: Mapping(Input("= 1 +", "x"), string.Empty)),
            End(),
            Flow("f0", "Start", "G"),
            Flow("f1", "G", "A", "= (x"),
            Flow("f2", "G", "End")));

        var failure = result is Engine.Results.Failure f ? f : throw new InvalidOperationException("expected a failure");
        var text = TestEngine.Describe(failure);
        text.ShouldContain("INVALID_EXPRESSION");
    }

    [Fact]
    public void Inclusive_reachability_follows_the_sequence_flows()
    {
        var process = new Bpmn.Model.BpmnProcess(
            "p",
            null,
            true,
            [
                new Bpmn.Model.StartEvent { Id = "S" },
                new Bpmn.Model.ManualTask { Id = "A" },
                new Bpmn.Model.ManualTask { Id = "B" },
                new Bpmn.Model.EndEvent { Id = "E" },
            ],
            [
                new Bpmn.Model.SequenceFlow("1", "S", "A", null),
                new Bpmn.Model.SequenceFlow("2", "A", "E", null),
                new Bpmn.Model.SequenceFlow("3", "B", "E", null),
            ]);
        var graph = new ProcessGraph(process);

        Engine.Handlers.InclusiveGatewayHandler.CanReach(graph, "S", "E").ShouldBeTrue();
        Engine.Handlers.InclusiveGatewayHandler.CanReach(graph, "A", "B").ShouldBeFalse();
    }
}
