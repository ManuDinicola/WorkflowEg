using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Flowd.Application.Security;
using Flowd.Infrastructure.Postgres.Execution;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>FR-SEC-009: callers with groups reach only the user tasks they are assignee or candidate of.</summary>
[Trait("Category", "Integration")]
public sealed class TaskVisibilityTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="review" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:userTask id="U"><bpmn:extensionElements><flowd:assignmentDefinition candidateGroups="approvers"/></bpmn:extensionElements></bpmn:userTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="U"/><bpmn:sequenceFlow id="f2" sourceRef="U" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<HttpClient> ClientAsync(TestApp app, string name, string roles, params string[] groups)
    {
        await using var dataSource = NpgsqlDataSource.Create(app.ConnectionString);
        var key = ApiKeyHasher.NewKey();
        await new PostgresApiKeyStore(dataSource).CreateAsync(ApiKeyHasher.Hash(key), new ApiKeyInfo(name, roles.Split(','), string.Empty, null, groups), DateTimeOffset.UtcNow, CancellationToken.None);
        var client = new HttpClient { BaseAddress = app.Client.BaseAddress };
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<JsonArray> TasksAsync(HttpClient client) =>
        JsonNode.Parse(await client.GetStringAsync(new Uri("/v1/user-tasks?state=CREATED", UriKind.Relative)))!["items"]!.AsArray();

    [Fact]
    public async Task FR_SEC_009_a_caller_with_groups_sees_and_works_only_on_its_candidate_tasks()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);
        using var admin = await ClientAsync(app, "root", "admin");
        using var alice = await ClientAsync(app, "alice", "operator", "approvers");
        using var bob = await ClientAsync(app, "bob", "operator", "sales");
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(Process)), "resources", "review.bpmn" } };
        (await admin.PostAsync(new Uri("/v1/deployments", UriKind.Relative), form)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new { bpmnProcessId = "review" })).EnsureSuccessStatusCode();
        var taskKey = (await TasksAsync(admin)).Single()!["key"]!.GetValue<string>();

        (await TasksAsync(alice)).Count.ShouldBe(1);
        (await TasksAsync(bob)).Count.ShouldBe(0);
        (await bob.PostAsJsonAsync(new Uri($"/v1/user-tasks/{taskKey}/claim", UriKind.Relative), new { user = "bob" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await bob.GetAsync(new Uri($"/v1/user-tasks/{taskKey}/form", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.PostAsJsonAsync(new Uri($"/v1/user-tasks/{taskKey}/assign", UriKind.Relative), new { user = "bob" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await alice.PostAsJsonAsync(new Uri($"/v1/user-tasks/{taskKey}/claim", UriKind.Relative), new { user = "mallory" })).EnsureSuccessStatusCode();
        (await TasksAsync(admin)).Single()!["assignee"]!.GetValue<string>().ShouldBe("alice"); // claims are always made as the caller
        (await alice.PostAsJsonAsync(new Uri($"/v1/user-tasks/{taskKey}/complete", UriKind.Relative), new { variables = new { ok = true } })).EnsureSuccessStatusCode();
        (await TasksAsync(admin)).Count.ShouldBe(0);
    }
}
