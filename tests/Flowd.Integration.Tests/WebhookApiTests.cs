using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Flowd.Integration.Tests;

/// <summary>FR-CON-011: signed inbound webhooks become messages.</summary>
[Trait("Category", "Integration")]
public sealed class WebhookApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:message id="M" name="order-paid"><bpmn:extensionElements><flowd:subscription correlationKey="= orderId"/></bpmn:extensionElements></bpmn:message>
          <bpmn:process id="w" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:intermediateCatchEvent id="Wait"><bpmn:messageEventDefinition messageRef="M"/></bpmn:intermediateCatchEvent>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Wait"/><bpmn:sequenceFlow id="f2" sourceRef="Wait" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static readonly string[] Settings = ["--Flowd:Connectors:Webhooks:order-paid:Secret=topsecret", "--Flowd:Connectors:Webhooks:order-paid:CorrelationKey=orderId"];

    private static async Task<HttpResponseMessage> PostAsync(TestApp app, string name, string body, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/v1/connectors/webhook/{name}", UriKind.Relative)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (signature is not null)
        {
            request.Headers.Add("X-Flowd-Signature", signature);
        }

        return await app.Client.SendAsync(request);
    }

    private static string Sign(string body, string secret = "topsecret") =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    [Fact]
    public async Task FR_CON_011_a_signed_webhook_publishes_a_message_that_wakes_the_waiting_instance()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true, Settings);
        await using var open = await TestApp.StartAsync(postgres, secure: false, Settings);
        await open.DeployAsync(Process);
        var key = await open.StartInstanceAsync("w", """{"orderId":"o-1"}""");
        const string body = """{"orderId":"o-1","amount":5}""";

        var response = await PostAsync(open, "order-paid", body, Sign(body));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await open.GetInstanceAsync(key)).State.ShouldBe("COMPLETED");
        (await open.GetVariablesAsync(key))["amount"]!.GetValue<int>().ShouldBe(5);
        (await PostAsync(app, "order-paid", body, Sign(body))).StatusCode.ShouldBe(HttpStatusCode.Accepted); // reachable without an API key even when authentication is on
    }

    [Fact]
    public async Task FR_CON_011_wrong_or_missing_signatures_unknown_hooks_and_bad_bodies_are_refused()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true, Settings);
        const string body = """{"orderId":"o-2"}""";

        (await PostAsync(app, "order-paid", body, Sign(body, "wrong"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(app, "order-paid", body, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(app, "order-paid", body, "sha256=zz")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(app, "nope", body, Sign(body))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(app, "order-paid", "[]", Sign("[]"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(app, "order-paid", """{"other":1}""", Sign("""{"other":1}"""))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
