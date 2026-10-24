using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flowd.Contracts.Deployments;
using Flowd.Contracts.Execution;

namespace Flowd.Integration.Tests;

/// <summary>M5 on a real PostgreSQL: error boundaries (T-ERR), signals (T-SIG) and user tasks (T-UT).</summary>
[Trait("Category", "Integration")]
public sealed class ErrorsSignalsUserTaskApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><bpmn:definitions xmlns:bpmn=\"http://www.omg.org/spec/BPMN/20100524/MODEL\" xmlns:flowd=\"http://flowd.io/schema/1.0\">";

    private static string UserTaskProcess() => Header + """
        <bpmn:process id="review" isExecutable="true">
          <bpmn:startEvent id="S"/>
          <bpmn:userTask id="Review"><bpmn:extensionElements><flowd:assignmentDefinition candidateGroups="managers"/><flowd:formDefinition formKey="review-form"/></bpmn:extensionElements></bpmn:userTask>
          <bpmn:endEvent id="E"/>
          <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Review"/>
          <bpmn:sequenceFlow id="f2" sourceRef="Review" targetRef="E"/>
        </bpmn:process></bpmn:definitions>
        """;

    private static string ErrorProcess() => Header + """
        <bpmn:error id="Err" name="declined" errorCode="PAYMENT_DECLINED"/>
        <bpmn:process id="pay" isExecutable="true">
          <bpmn:startEvent id="S"/>
          <bpmn:serviceTask id="Pay"><bpmn:extensionElements><flowd:taskDefinition type="pay"/></bpmn:extensionElements></bpmn:serviceTask>
          <bpmn:boundaryEvent id="Declined" attachedToRef="Pay"><bpmn:errorEventDefinition errorRef="Err"/></bpmn:boundaryEvent>
          <bpmn:serviceTask id="Notify"><bpmn:extensionElements><flowd:taskDefinition type="notify"/></bpmn:extensionElements></bpmn:serviceTask>
          <bpmn:endEvent id="Ok"/><bpmn:endEvent id="Failed"/>
          <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Pay"/>
          <bpmn:sequenceFlow id="f2" sourceRef="Pay" targetRef="Ok"/>
          <bpmn:sequenceFlow id="f3" sourceRef="Declined" targetRef="Notify"/>
          <bpmn:sequenceFlow id="f4" sourceRef="Notify" targetRef="Failed"/>
        </bpmn:process></bpmn:definitions>
        """;

    private static string SignalProcess() => Header + """
        <bpmn:signal id="Sig" name="go"/>
        <bpmn:process id="waiter" isExecutable="true">
          <bpmn:startEvent id="S"/>
          <bpmn:intermediateCatchEvent id="Wait"><bpmn:signalEventDefinition signalRef="Sig"/></bpmn:intermediateCatchEvent>
          <bpmn:endEvent id="E"/>
          <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Wait"/>
          <bpmn:sequenceFlow id="f2" sourceRef="Wait" targetRef="E"/>
        </bpmn:process></bpmn:definitions>
        """;

    [Fact]
    public async Task T_UT_01_claim_conflict_and_completion_through_the_api()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(UserTaskProcess());
        var instance = await app.StartInstanceAsync("review");

        var list = await app.Client.GetFromJsonAsync<PageDto<UserTaskDto>>(new Uri("/v1/user-tasks?candidateGroup=managers&state=CREATED", UriKind.Relative));
        var task = list!.Items.ShouldHaveSingleItem();
        task.FormKey.ShouldBe("review-form");
        task.ProcessInstanceKey.ShouldBe(instance);

        (await app.Client.PostAsJsonAsync(new Uri($"/v1/user-tasks/{task.Key}/claim", UriKind.Relative), new ClaimUserTaskRequestDto("alice"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var conflict = await app.Client.PostAsJsonAsync(new Uri($"/v1/user-tasks/{task.Key}/claim", UriKind.Relative), new ClaimUserTaskRequestDto("bob"));
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await TestApp.ProblemCodeAsync(conflict)).ShouldBe("USER_TASK_ALREADY_CLAIMED");

        var complete = await app.Client.PostAsJsonAsync(
            new Uri($"/v1/user-tasks/{task.Key}/complete", UriKind.Relative),
            new CompleteUserTaskRequestDto(JsonNode.Parse("{\"approved\":true}")!.AsObject(), "alice"));

        complete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.GetInstanceAsync(instance)).State.ShouldBe("COMPLETED");
        (await app.GetVariablesAsync(instance))["approved"]!.GetValue<bool>().ShouldBeTrue();
        (await app.Client.PostAsync(new Uri("/v1/user-tasks/12345/unclaim", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task T_ERR_01_a_thrown_job_error_is_caught_by_the_boundary_event()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(ErrorProcess());
        var instance = await app.StartInstanceAsync("pay");
        var job = (await app.ActivateAsync("pay")).Single();

        var thrown = await app.Client.PostAsJsonAsync(new Uri($"/v1/jobs/{job.Key}/error", UriKind.Relative), new ThrowJobErrorRequestDto("PAYMENT_DECLINED", "declined", null, null));

        thrown.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var notify = (await app.ActivateAsync("notify")).ShouldHaveSingleItem();
        await app.CompleteAsync(notify.Key);
        (await app.GetInstanceAsync(instance)).State.ShouldBe("COMPLETED");
    }

    [Fact]
    public async Task T_ERR_03_an_unhandled_job_error_creates_an_incident()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(ErrorProcess());
        var instance = await app.StartInstanceAsync("pay");
        var job = (await app.ActivateAsync("pay")).Single();

        await app.Client.PostAsJsonAsync(new Uri($"/v1/jobs/{job.Key}/error", UriKind.Relative), new ThrowJobErrorRequestDto("OTHER_CODE", null, null, null));

        var incidents = await app.Client.GetFromJsonAsync<PageDto<IncidentDto>>(new Uri($"/v1/incidents?processInstanceKey={instance}&state=OPEN", UriKind.Relative));
        incidents!.Items.ShouldHaveSingleItem().ErrorType.ShouldBe("UNHANDLED_ERROR_EVENT");
    }

    [Fact]
    public async Task T_SIG_01_a_broadcast_signal_wakes_the_waiting_instances()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(SignalProcess());
        var first = await app.StartInstanceAsync("waiter");
        var second = await app.StartInstanceAsync("waiter");

        var response = await app.Client.PostAsJsonAsync(new Uri("/v1/signals", UriKind.Relative), new BroadcastSignalRequestDto("go", JsonNode.Parse("{\"by\":\"ops\"}")!.AsObject()));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<BroadcastSignalResponseDto>())!.Triggered.ShouldBe(2);
        (await app.GetInstanceAsync(first)).State.ShouldBe("COMPLETED");
        (await app.GetInstanceAsync(second)).State.ShouldBe("COMPLETED");
        (await app.GetVariablesAsync(first))["by"]!.GetValue<string>().ShouldBe("ops");
    }
}
