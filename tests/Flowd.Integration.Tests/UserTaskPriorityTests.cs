using System.Net.Http.Json;
using Flowd.Contracts.Deployments;
using Flowd.Contracts.Execution;

namespace Flowd.Integration.Tests;

/// <summary>FR-UT-004: task priority, follow-up date and search filters.</summary>
[Trait("Category", "Integration")]
public sealed class UserTaskPriorityTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static string Process(string id, int priority) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="{id}" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:userTask id="U"><bpmn:extensionElements><flowd:taskSchedule dueDate="2030-01-01T00:00:00Z" followUpDate="2029-12-01T00:00:00Z" priority="{priority}"/></bpmn:extensionElements></bpmn:userTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="U"/><bpmn:sequenceFlow id="f2" sourceRef="U" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public async Task FR_UT_004_priority_and_follow_up_date_are_exposed_and_can_be_filtered()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process("urgent", 90));
        await app.DeployAsync(Process("normal", 20));
        await app.StartInstanceAsync("urgent");
        await app.StartInstanceAsync("normal");

        var all = (await app.Client.GetFromJsonAsync<PageDto<UserTaskDto>>(new Uri("/v1/user-tasks", UriKind.Relative)))!;
        var urgent = (await app.Client.GetFromJsonAsync<PageDto<UserTaskDto>>(new Uri("/v1/user-tasks?minPriority=50", UriKind.Relative)))!;
        var early = (await app.Client.GetFromJsonAsync<PageDto<UserTaskDto>>(new Uri("/v1/user-tasks?dueBefore=2020-01-01T00:00:00Z", UriKind.Relative)))!;

        all.Items.Count.ShouldBe(2);
        urgent.Items.Single().Priority.ShouldBe(90);
        urgent.Items.Single().FollowUpDate.ShouldNotBeNull();
        early.Items.ShouldBeEmpty();
    }
}
