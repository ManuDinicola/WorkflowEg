using System.Net;
using System.Net.Http.Json;
using System.Text;
using Flowd.Contracts.Deployments;
using Flowd.Contracts.Execution;

namespace Flowd.Integration.Tests;

/// <summary>API-R1 idempotency keys and FR-SEC-004 input and rate limits.</summary>
[Trait("Category", "Integration")]
public sealed class LimitsApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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

    private static HttpRequestMessage Start(string key, string variablesJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/process-instances", UriKind.Relative))
        {
            Content = new StringContent($"{{\"bpmnProcessId\":\"p\",\"variables\":{variablesJson}}}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Fact]
    public async Task API_R1_a_repeated_idempotency_key_replays_the_first_response_and_starts_one_instance()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);

        using var first = await app.Client.SendAsync(Start("k-1", "{\"a\":1}"));
        using var second = await app.Client.SendAsync(Start("k-1", "{\"a\":1}"));

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.Contains("Idempotent-Replayed").ShouldBeTrue();
        (await second.Content.ReadAsStringAsync()).ShouldBe(await first.Content.ReadAsStringAsync());
        var list = await app.Client.GetFromJsonAsync<PageDto<InstanceDto>>(new Uri("/v1/process-instances", UriKind.Relative));
        list!.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task API_R1_the_same_key_with_a_different_request_is_refused()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        using var first = await app.Client.SendAsync(Start("k-2", "{\"a\":1}"));

        using var other = await app.Client.SendAsync(Start("k-2", "{\"a\":2}"));

        other.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await TestApp.ProblemCodeAsync(other)).ShouldBe("IDEMPOTENCY_KEY_REUSED");
        using var none = await app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/process-instances", UriKind.Relative))
        {
            Content = JsonContent.Create(new StartInstanceRequestDto("p", null, null, null, null)),
        });
        none.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FR_SEC_004_requests_over_4_mib_are_rejected()
    {
        await using var app = await TestApp.StartAsync(postgres);
        using var content = new StringContent("{\"bpmnProcessId\":\"p\",\"variables\":{\"big\":\"" + new string('x', 5 * 1024 * 1024) + "\"}}", Encoding.UTF8, "application/json");

        // Kestrel either answers 413 or, on some platforms, closes the connection while the client is still sending.
        var status = HttpStatusCode.RequestEntityTooLarge;
        try
        {
            status = (await app.Client.PostAsync(new Uri("/v1/process-instances", UriKind.Relative), content)).StatusCode;
        }
        catch (HttpRequestException)
        {
            // connection closed by the server: the request was refused
        }

        status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task FR_SEC_004_the_token_bucket_returns_429_and_leaves_health_checks_alone()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false, "--Flowd:RateLimit:TokensPerSecond=1", "--Flowd:RateLimit:Burst=3");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++)
        {
            statuses.Add((await app.Client.GetAsync(new Uri("/v1/process-definitions", UriKind.Relative))).StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
        var rejected = default(HttpResponseMessage);
        statuses.Take(3).ShouldAllBe(s => s == HttpStatusCode.OK);
        // Keep asking until a 429 comes back: one token is refilled per second, so a slow machine can let a single request through.
        for (var i = 0; i < 50 && rejected?.StatusCode != HttpStatusCode.TooManyRequests; i++)
        {
            rejected = await app.Client.GetAsync(new Uri("/v1/process-definitions", UriKind.Relative));
        }

        (await TestApp.ProblemCodeAsync(rejected!)).ShouldBe("RESOURCE_EXHAUSTED");
        for (var i = 0; i < 10; i++)
        {
            (await app.Client.GetAsync(new Uri("/healthz", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }
}
