using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowd.Contracts.Deployments;
using Flowd.Contracts.Execution;
using Flowd.Host;
using Flowd.Infrastructure.Postgres;
using Microsoft.AspNetCore.Builder;

namespace Flowd.Integration.Tests;

/// <summary>A running Flowd host on a fresh, migrated database, with small helpers for the HTTP API.</summary>
public sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApp(WebApplication app, HttpClient client, string connectionString)
    {
        _app = app;
        Client = client;
        ConnectionString = connectionString;
    }

    public HttpClient Client { get; }

    public IReadOnlyList<string> Urls => _app.Urls.ToList();

    public string ConnectionString { get; }

    public static async Task<TestApp> StartAsync(PostgresFixture postgres, bool secure = false, params string[] extraArgs)
    {
        var cs = await postgres.CreateDatabaseAsync();
        new PostgresMigrationRunner(cs).Migrate();
        string[] args = ["--urls", "http://127.0.0.1:0", $"--Flowd:ConnectionString={cs}", .. secure ? Array.Empty<string>() : ["--Flowd:Auth:Insecure=true"], .. extraArgs];
        var app = FlowdHostBuilder.Build(args);
        await app.StartAsync();
        return new TestApp(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(60) }, cs);
    }

    public async Task<string> DeployAsync(string xml)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(xml));
        form.Add(file, "resources", "test.bpmn");
        var response = await Client.PostAsync(new Uri("/v1/deployments", UriKind.Relative), form);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DeploymentResponse>())!.Processes.Single().Definition.Key;
    }

    public async Task<string> StartInstanceAsync(string processId, string? variablesJson = null)
    {
        var body = new StartInstanceRequestDto(processId, null, variablesJson is null ? null : JsonNode.Parse(variablesJson)!.AsObject(), null, null);
        var response = await Client.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<StartInstanceResponseDto>())!.ProcessInstanceKey;
    }

    public async Task<IReadOnlyList<JobDto>> ActivateAsync(string type, string worker = "w1", int maxJobs = 10, long requestTimeoutMs = 0, long timeoutMs = 30_000)
    {
        var body = new ActivateJobsRequestDto(type, worker, timeoutMs, maxJobs, requestTimeoutMs, null);
        var response = await Client.PostAsJsonAsync(new Uri("/v1/jobs/activate", UriKind.Relative), body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ActivateJobsResponseDto>())!.Jobs;
    }

    public Task<HttpResponseMessage> CompleteAsync(string jobKey, string? variablesJson = null, string? worker = null) =>
        Client.PostAsJsonAsync(
            new Uri($"/v1/jobs/{jobKey}/complete", UriKind.Relative),
            new CompleteJobRequestDto(variablesJson is null ? null : JsonNode.Parse(variablesJson)!.AsObject(), worker));

    public async Task<InstanceDto> GetInstanceAsync(string key) =>
        (await Client.GetFromJsonAsync<InstanceDto>(new Uri($"/v1/process-instances/{key}", UriKind.Relative)))!;

    public async Task<JsonObject> GetVariablesAsync(string key) =>
        (await Client.GetFromJsonAsync<JsonObject>(new Uri($"/v1/process-instances/{key}/variables", UriKind.Relative)))!;

    public static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
