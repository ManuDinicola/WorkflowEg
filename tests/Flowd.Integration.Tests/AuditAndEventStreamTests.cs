using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Flowd.Application.Security;
using Flowd.Infrastructure.Postgres.Execution;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>FR-SEC-005 audit log and FR-API-010 server-sent event stream.</summary>
[Trait("Category", "Integration")]
public sealed class AuditAndEventStreamTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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

    private static async Task<string> CreateKeyAsync(string connectionString, string name, string roles)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var key = ApiKeyHasher.NewKey();
        await new PostgresApiKeyStore(dataSource).CreateAsync(ApiKeyHasher.Hash(key), new ApiKeyInfo(name, roles.Split(','), string.Empty), DateTimeOffset.UtcNow, CancellationToken.None);
        return key;
    }

    private static HttpClient As(TestApp app, string key)
    {
        var client = new HttpClient { BaseAddress = app.Client.BaseAddress };
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    [Fact]
    public async Task FR_SEC_005_mutating_calls_are_audited_with_the_caller_and_only_admins_can_read_the_log()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);
        using var admin = As(app, await CreateKeyAsync(app.ConnectionString, "alice", "admin"));
        using var reader = As(app, await CreateKeyAsync(app.ConnectionString, "bob", "reader"));
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Process));
        form.Add(file, "resources", "p.bpmn");
        (await admin.PostAsync(new Uri("/v1/deployments", UriKind.Relative), form)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync(new Uri("/v1/process-definitions", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var log = (await admin.GetFromJsonAsync<JsonObject>(new Uri("/v1/audit", UriKind.Relative)))!;
        var items = log["items"]!.AsArray();

        items.Count.ShouldBe(1); // the GET is not audited
        items[0]!["actor"]!.GetValue<string>().ShouldBe("alice");
        items[0]!["method"]!.GetValue<string>().ShouldBe("POST");
        items[0]!["path"]!.GetValue<string>().ShouldBe("/v1/deployments");
        items[0]!["status"]!.GetValue<int>().ShouldBe(200);
        (await reader.GetAsync(new Uri("/v1/audit", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task FR_API_010_the_event_stream_delivers_lifecycle_events_in_order_and_can_be_resumed()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        await app.StartInstanceAsync("p");

        var first = await ReadEventsAsync(app, "/v1/events/stream", expected: 3);

        first.Count.ShouldBeGreaterThanOrEqualTo(3);
        first.Select(e => e.Seq).ShouldBe(first.Select(e => e.Seq).Order().ToList());
        var resumed = await ReadEventsAsync(app, $"/v1/events/stream?after={first[0].Seq}", expected: 1);
        resumed[0].Seq.ShouldBeGreaterThan(first[0].Seq);
    }

    private static async Task<List<(long Seq, string Type)>> ReadEventsAsync(TestApp app, string url, int expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        using var response = await app.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var events = new List<(long, string)>();
        long seq = 0;
        while (events.Count < expected && await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                seq = long.Parse(line[4..], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                events.Add((seq, line[7..]));
            }
        }

        return events;
    }
}
