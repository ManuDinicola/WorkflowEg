using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flowd.Contracts.Deployments;
using Flowd.Contracts.Execution;

namespace Flowd.Integration.Tests;

/// <summary>M6 on a real PostgreSQL: call activities, multi-instance and event sub-processes.</summary>
[Trait("Category", "Integration")]
public sealed class CompositionApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><bpmn:definitions xmlns:bpmn=\"http://www.omg.org/spec/BPMN/20100524/MODEL\" xmlns:flowd=\"http://flowd.io/schema/1.0\">";

    private static string Child() => Header + """
        <bpmn:process id="child" isExecutable="true">
          <bpmn:startEvent id="S"/>
          <bpmn:serviceTask id="Work"><bpmn:extensionElements><flowd:taskDefinition type="child-work"/></bpmn:extensionElements></bpmn:serviceTask>
          <bpmn:endEvent id="E"/>
          <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Work"/><bpmn:sequenceFlow id="f2" sourceRef="Work" targetRef="E"/>
        </bpmn:process></bpmn:definitions>
        """;

    private static string Parent() => Header + """
        <bpmn:process id="parent" isExecutable="true">
          <bpmn:startEvent id="S"/>
          <bpmn:callActivity id="Call" calledElement="child"/>
          <bpmn:endEvent id="E"/>
          <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Call"/><bpmn:sequenceFlow id="f2" sourceRef="Call" targetRef="E"/>
        </bpmn:process></bpmn:definitions>
        """;

    private static string MultiInstance() => Header + """
        <bpmn:process id="mi" isExecutable="true">
          <bpmn:startEvent id="S"/>
          <bpmn:serviceTask id="Each">
            <bpmn:extensionElements><flowd:taskDefinition type="each"/></bpmn:extensionElements>
            <bpmn:multiInstanceLoopCharacteristics><bpmn:extensionElements><flowd:loopCharacteristics inputCollection="= items" inputElement="item" outputCollection="results" outputElement="= out"/></bpmn:extensionElements></bpmn:multiInstanceLoopCharacteristics>
          </bpmn:serviceTask>
          <bpmn:endEvent id="E"/>
          <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Each"/><bpmn:sequenceFlow id="f2" sourceRef="Each" targetRef="E"/>
        </bpmn:process></bpmn:definitions>
        """;

    private static string EventSubProcess() => Header + """
        <bpmn:message id="M1" name="stop"><bpmn:extensionElements><flowd:subscription correlationKey="= id"/></bpmn:extensionElements></bpmn:message>
        <bpmn:process id="esp" isExecutable="true">
          <bpmn:startEvent id="S"/>
          <bpmn:serviceTask id="Work"><bpmn:extensionElements><flowd:taskDefinition type="work"/></bpmn:extensionElements></bpmn:serviceTask>
          <bpmn:endEvent id="E"/>
          <bpmn:subProcess id="OnStop" triggeredByEvent="true">
            <bpmn:startEvent id="OS" isInterrupting="true"><bpmn:messageEventDefinition messageRef="M1"/></bpmn:startEvent>
            <bpmn:manualTask id="Cleanup"/><bpmn:endEvent id="OE"/>
            <bpmn:sequenceFlow id="o1" sourceRef="OS" targetRef="Cleanup"/><bpmn:sequenceFlow id="o2" sourceRef="Cleanup" targetRef="OE"/>
          </bpmn:subProcess>
          <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Work"/><bpmn:sequenceFlow id="f2" sourceRef="Work" targetRef="E"/>
        </bpmn:process></bpmn:definitions>
        """;

    [Fact]
    public async Task T_CALL_01_parent_and_child_run_across_instances_and_variables_flow_both_ways()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Child());
        await app.DeployAsync(Parent());
        var parent = await app.StartInstanceAsync("parent", "{\"order\":7}");

        var job = (await app.ActivateAsync("child-work")).ShouldHaveSingleItem();
        job.ProcessInstanceKey.ShouldNotBe(parent);
        job.Variables["order"]!.GetValue<int>().ShouldBe(7);
        (await app.GetInstanceAsync(parent)).State.ShouldBe("ACTIVE");

        await app.CompleteAsync(job.Key, "{\"result\":\"ok\"}");

        (await app.GetInstanceAsync(job.ProcessInstanceKey)).State.ShouldBe("COMPLETED");
        (await app.GetInstanceAsync(parent)).State.ShouldBe("COMPLETED");
        (await app.GetVariablesAsync(parent))["result"]!.GetValue<string>().ShouldBe("ok");
    }

    [Fact]
    public async Task T_CALL_02_cancelling_the_parent_cancels_the_child()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Child());
        await app.DeployAsync(Parent());
        var parent = await app.StartInstanceAsync("parent");
        var job = (await app.ActivateAsync("child-work")).Single();

        (await app.Client.PostAsync(new Uri($"/v1/process-instances/{parent}/cancel", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await app.GetInstanceAsync(parent)).State.ShouldBe("TERMINATED");
        (await app.GetInstanceAsync(job.ProcessInstanceKey)).State.ShouldBe("TERMINATED");
    }

    [Fact]
    public async Task T_CALL_a_missing_child_process_raises_an_incident_that_resolves_after_deploying_it()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Parent());
        var parent = await app.StartInstanceAsync("parent");
        var incidents = await app.Client.GetFromJsonAsync<PageDto<IncidentDto>>(new Uri($"/v1/incidents?processInstanceKey={parent}&state=OPEN", UriKind.Relative));
        var incident = incidents!.Items.ShouldHaveSingleItem();
        incident.ErrorType.ShouldBe("CALLED_ELEMENT_ERROR");

        await app.DeployAsync(Child());
        (await app.Client.PostAsync(new Uri($"/v1/incidents/{incident.Key}/resolve", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await app.ActivateAsync("child-work")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task T_MI_01_parallel_multi_instance_collects_outputs_in_input_order()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(MultiInstance());
        var instance = await app.StartInstanceAsync("mi", "{\"items\":[\"a\",\"b\",\"c\"]}");

        var jobs = await app.ActivateAsync("each");
        jobs.Count.ShouldBe(3);
        foreach (var job in jobs.Reverse())
        {
            var item = job.Variables["item"]!.GetValue<string>();
            (await app.CompleteAsync(job.Key, $"{{\"out\":\"{item}!\"}}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await app.GetInstanceAsync(instance)).State.ShouldBe("COMPLETED");
        (await app.GetVariablesAsync(instance))["results"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["a!", "b!", "c!"]);
    }

    [Fact]
    public async Task T_MI_01_completing_all_iterations_concurrently_finishes_the_body_exactly_once()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(MultiInstance());
        var instance = await app.StartInstanceAsync("mi", "{\"items\":[1,2,3,4,5]}");
        var jobs = await app.ActivateAsync("each");

        var responses = await Task.WhenAll(jobs.Select(j => app.CompleteAsync(j.Key, "{\"out\":1}")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent);
        (await app.GetInstanceAsync(instance)).State.ShouldBe("COMPLETED");
        (await app.GetVariablesAsync(instance))["results"]!.AsArray().Count.ShouldBe(5);
    }

    [Fact]
    public async Task T_ESP_01_an_interrupting_message_event_sub_process_takes_over_the_instance()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(EventSubProcess());
        var instance = await app.StartInstanceAsync("esp", "{\"id\":\"x\"}");
        var work = (await app.ActivateAsync("work")).Single();

        var published = await app.Client.PostAsJsonAsync(new Uri("/v1/messages", UriKind.Relative), new PublishMessageRequestDto("stop", "x", null, null, null));

        published.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.GetInstanceAsync(instance)).State.ShouldBe("COMPLETED");
        (await app.CompleteAsync(work.Key)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
