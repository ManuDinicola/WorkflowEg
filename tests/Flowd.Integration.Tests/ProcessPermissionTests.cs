using System.Net;
using System.Net.Http.Json;
using System.Text;
using Flowd.Application.Security;
using Flowd.Contracts.Execution;
using Flowd.Infrastructure.Postgres.Execution;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>FR-SEC-006: an API key limited to certain process ids.</summary>
[Trait("Category", "Integration")]
public sealed class ProcessPermissionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static string Process(string id) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="{id}" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="work"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<string> CreateKeyAsync(string connectionString, string roles, params string[] processes)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var key = ApiKeyHasher.NewKey();
        await new PostgresApiKeyStore(dataSource).CreateAsync(ApiKeyHasher.Hash(key), new ApiKeyInfo("k", roles.Split(','), string.Empty, processes), DateTimeOffset.UtcNow, CancellationToken.None);
        return key;
    }

    private static HttpClient As(TestApp app, string key)
    {
        var client = new HttpClient { BaseAddress = app.Client.BaseAddress };
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<HttpResponseMessage> DeployAsync(HttpClient client, string id)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Process(id)));
        form.Add(file, "resources", id + ".bpmn");
        return await client.PostAsync(new Uri("/v1/deployments", UriKind.Relative), form);
    }

    [Fact]
    public async Task FR_SEC_006_a_limited_key_can_only_deploy_start_and_batch_its_own_processes()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);
        using var admin = As(app, await CreateKeyAsync(app.ConnectionString, "admin"));
        using var limited = As(app, await CreateKeyAsync(app.ConnectionString, "admin", "allowed"));
        (await DeployAsync(admin, "other")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await DeployAsync(limited, "other")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await (await DeployAsync(limited, "other")).Content.ReadAsStringAsync()).ShouldContain("PROCESS_NOT_ALLOWED");
        (await DeployAsync(limited, "allowed")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await limited.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new StartInstanceRequestDto("other", null, null, null, null))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await limited.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new StartInstanceRequestDto("allowed", null, null, null, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await limited.PostAsJsonAsync(new Uri("/v1/batch/cancel-instances", UriKind.Relative), new { })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await limited.PostAsJsonAsync(new Uri("/v1/batch/cancel-instances", UriKind.Relative), new { bpmnProcessId = "other" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await limited.PostAsJsonAsync(new Uri("/v1/batch/cancel-instances", UriKind.Relative), new { bpmnProcessId = "allowed" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new StartInstanceRequestDto("other", null, null, null, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
