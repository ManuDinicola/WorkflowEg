using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Flowd.Integration.Tests;

/// <summary>FR-CON-010: a service task with the built-in HTTP connector calls a real HTTP service and stores the answer.</summary>
[Trait("Category", "Integration")]
public sealed class ConnectorApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static string Process(string url) => ProcessTemplate.Replace("__URL__", url, StringComparison.Ordinal);

    private const string ProcessTemplate = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="c" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="Call"><bpmn:extensionElements>
              <flowd:connector type="http" url="__URL__" method="POST" body='{"id":{{id}}}' headers='{"X-Token":"{{secrets.TOKEN}}"}' resultVariable="answer"/>
            </bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Call"/><bpmn:sequenceFlow id="f2" sourceRef="Call" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<(WebApplication App, string Url, List<(string Body, string? Token)> Requests)> FakeServiceAsync()
    {
        var requests = new List<(string, string?)>();
        var app = WebApplication.CreateSlimBuilder().Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapPost("/orders", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            requests.Add((await reader.ReadToEndAsync(), request.Headers["X-Token"].ToString()));
            return Results.Json(new { accepted = true });
        });
        await app.StartAsync();
        return (app, app.Urls.First(), requests);
    }

    [Fact]
    public async Task FR_CON_010_the_connector_calls_the_service_with_templated_body_and_secret_and_the_process_continues()
    {
        var (service, url, requests) = await FakeServiceAsync();
        await using var _ = service;
        await using var app = await TestApp.StartAsync(postgres, secure: false, "--Flowd:Connectors:AllowedHosts:0=127.0.0.1", "--Flowd:Secrets:TOKEN=s3cret");
        await app.DeployAsync(Process(url + "/orders"));

        var key = await app.StartInstanceAsync("c", """{"id":99}""");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await app.GetInstanceAsync(key)).State != "COMPLETED")
        {
            await Task.Delay(100, timeout.Token);
        }

        requests.Single().ShouldBe(("""{"id":99}""", "s3cret"));
        var answer = (await app.GetVariablesAsync(key))["answer"]!.AsObject();
        answer["status"]!.GetValue<int>().ShouldBe(200);
        answer["body"]!["accepted"]!.GetValue<bool>().ShouldBeTrue();
        (await app.GetVariablesAsync(key)).ToJsonString().ShouldNotContain("s3cret");
    }

    [Fact]
    public async Task FR_CON_010_a_host_that_is_not_allowed_ends_in_an_incident_not_in_a_call()
    {
        var (service, url, requests) = await FakeServiceAsync();
        await using var _ = service;
        await using var app = await TestApp.StartAsync(postgres); // no AllowedHosts configured
        await app.DeployAsync(Process(url + "/orders"));
        var key = await app.StartInstanceAsync("c", """{"id":1}""");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        JsonNode? incidents = null;
        while (incidents is null || incidents["items"]!.AsArray().Count == 0)
        {
            await Task.Delay(200, timeout.Token);
            incidents = JsonNode.Parse(await app.Client.GetStringAsync(new Uri($"/v1/incidents?processInstanceKey={key}", UriKind.Relative), timeout.Token));
        }

        incidents["items"]![0]!["errorMessage"]!.GetValue<string>().ShouldContain("AllowedHosts");
        requests.ShouldBeEmpty();
    }
}
