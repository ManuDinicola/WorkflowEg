using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Flowd.Application.Security;
using Flowd.Contracts.Deployments;
using Flowd.Contracts.Execution;
using Flowd.Infrastructure.Postgres.Execution;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>M7 security: authentication on by default, roles, tenancy (T-SEC-01, T-SEC-02, FR-SEC-001..003).</summary>
[Trait("Category", "Integration")]
public sealed class SecurityApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="p" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="work"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<string> CreateKeyAsync(string connectionString, string name, string roles, string tenant)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var key = ApiKeyHasher.NewKey();
        await new PostgresApiKeyStore(dataSource).CreateAsync(ApiKeyHasher.Hash(key), new ApiKeyInfo(name, roles.Split(','), tenant), DateTimeOffset.UtcNow, CancellationToken.None);
        return key;
    }

    private static HttpClient As(TestApp app, string key)
    {
        var client = new HttpClient { BaseAddress = app.Client.BaseAddress };
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<HttpResponseMessage> DeployAsync(HttpClient client)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Process));
        form.Add(file, "resources", "p.bpmn");
        return await client.PostAsync(new Uri("/v1/deployments", UriKind.Relative), form);
    }

    [Fact]
    public async Task FR_SEC_001_authentication_is_on_by_default_and_health_stays_open()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);

        var anonymous = await app.Client.GetAsync(new Uri("/v1/process-definitions", UriKind.Relative));
        var unknownKey = await As(app, "flowd_nope").GetAsync(new Uri("/v1/process-definitions", UriKind.Relative));
        var health = await app.Client.GetAsync(new Uri("/healthz", UriKind.Relative));

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TestApp.ProblemCodeAsync(anonymous)).ShouldBe("UNAUTHENTICATED");
        unknownKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task T_SEC_02_a_worker_can_run_jobs_but_cannot_deploy_or_cancel()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);
        var admin = As(app, await CreateKeyAsync(app.ConnectionString, "admin", "admin", string.Empty));
        var worker = As(app, await CreateKeyAsync(app.ConnectionString, "worker", "worker", string.Empty));
        (await DeployAsync(admin)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var started = await admin.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new StartInstanceRequestDto("p", null, null, null, null));
        var instance = (await started.Content.ReadFromJsonAsync<StartInstanceResponseDto>())!.ProcessInstanceKey;

        var denied = await DeployAsync(worker);
        var cancel = await worker.PostAsync(new Uri($"/v1/process-instances/{instance}/cancel", UriKind.Relative), null);
        var activate = await worker.PostAsJsonAsync(new Uri("/v1/jobs/activate", UriKind.Relative), new ActivateJobsRequestDto("work", "w", 30000, 5, 0, null));

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await TestApp.ProblemCodeAsync(denied)).ShouldBe("PERMISSION_DENIED");
        cancel.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        activate.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await activate.Content.ReadFromJsonAsync<ActivateJobsResponseDto>())!.Jobs.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task T_SEC_01_tenants_cannot_see_or_change_each_others_data()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);
        var tenantA = As(app, await CreateKeyAsync(app.ConnectionString, "a", "admin", "tenant-a"));
        var tenantB = As(app, await CreateKeyAsync(app.ConnectionString, "b", "admin", "tenant-b"));
        var deployed = await DeployAsync(tenantA);
        var definitionKey = (await deployed.Content.ReadFromJsonAsync<DeploymentResponse>())!.Processes.Single().Definition.Key;
        var started = await tenantA.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new StartInstanceRequestDto("p", null, null, null, null));
        var instance = (await started.Content.ReadFromJsonAsync<StartInstanceResponseDto>())!.ProcessInstanceKey;
        var job = (await (await tenantA.PostAsJsonAsync(new Uri("/v1/jobs/activate", UriKind.Relative), new ActivateJobsRequestDto("work", "w", 30000, 1, 0, null))).Content.ReadFromJsonAsync<ActivateJobsResponseDto>())!.Jobs.Single();

        (await tenantB.GetAsync(new Uri($"/v1/process-instances/{instance}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await tenantB.GetAsync(new Uri($"/v1/process-definitions/{definitionKey}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await tenantB.PostAsync(new Uri($"/v1/process-instances/{instance}/cancel", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await tenantB.PostAsJsonAsync(new Uri($"/v1/jobs/{job.Key}/complete", UriKind.Relative), new CompleteJobRequestDto(null, null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var list = await tenantB.GetFromJsonAsync<PageDto<InstanceDto>>(new Uri("/v1/process-instances", UriKind.Relative));
        list!.Items.ShouldBeEmpty();
        var other = await tenantB.PostAsJsonAsync(new Uri("/v1/jobs/activate", UriKind.Relative), new ActivateJobsRequestDto("work", "w", 30000, 1, 0, null));
        (await other.Content.ReadFromJsonAsync<ActivateJobsResponseDto>())!.Jobs.ShouldBeEmpty();
        (await tenantA.GetAsync(new Uri($"/v1/process-instances/{instance}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FR_SEC_001_jwt_bearer_tokens_with_role_and_tenant_claims_are_accepted()
    {
        var signingKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("a-test-signing-key-of-at-least-32-bytes!!"));
        await using var app = await TestApp.StartAsync(postgres, secure: true, $"--Flowd:Auth:JwtSigningKey={signingKey}", "--Flowd:Auth:JwtIssuer=test-issuer");
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "test-issuer",
            audience: null,
            claims: [new Claim("role", "developer"), new Claim("tenant", "tenant-j")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(signingKey)), SecurityAlgorithms.HmacSha256)));
        using var client = new HttpClient { BaseAddress = app.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await DeployAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(new Uri("/v1/jobs/activate", UriKind.Relative), new ActivateJobsRequestDto("work", "w", 30000, 1, 0, null))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var otherTenant = As(app, await CreateKeyAsync(app.ConnectionString, "x", "admin", "other"));
        var seenElsewhere = await otherTenant.GetFromJsonAsync<PageDto<ProcessDefinitionDto>>(new Uri("/v1/process-definitions", UriKind.Relative));
        seenElsewhere!.Items.ShouldBeEmpty();
        var forged = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("test-issuer", claims: [new Claim("role", "admin")], expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-different-key-of-at-least-32-bytes!!!!")), SecurityAlgorithms.HmacSha256)));
        using var attacker = new HttpClient { BaseAddress = app.Client.BaseAddress };
        attacker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);
        (await attacker.GetAsync(new Uri("/v1/process-definitions", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
