using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>FR-JOB-010: jobs are pushed over a held-open connection.</summary>
[Trait("Category", "Integration")]
public sealed class JobStreamTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="p" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="streamed"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public async Task FR_JOB_010_jobs_created_after_the_stream_opened_are_pushed_and_can_be_completed()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/jobs/stream?type=streamed&worker=w1&fetchVariables=keep", UriKind.Relative));
        using var response = await app.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        var instance = await app.StartInstanceAsync("p", """{"keep":1,"drop":2}""");

        string? data = null;
        while (data is null && await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line[6..];
            }
        }

        var job = JsonNode.Parse(data!)!;
        job["type"]!.GetValue<string>().ShouldBe("streamed");
        job["variables"]!.AsObject().ContainsKey("keep").ShouldBeTrue();
        job["variables"]!.AsObject().ContainsKey("drop").ShouldBeFalse();
        (await app.CompleteAsync(job["key"]!.GetValue<string>())).EnsureSuccessStatusCode();
        (await app.GetInstanceAsync(instance)).State.ShouldBe("COMPLETED");
    }

    [Fact]
    public async Task FR_JOB_010_the_stream_needs_a_type_and_a_worker()
    {
        await using var app = await TestApp.StartAsync(postgres);

        var response = await app.Client.GetAsync(new Uri("/v1/jobs/stream?type=&worker=", UriKind.Relative));

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
    }
}
