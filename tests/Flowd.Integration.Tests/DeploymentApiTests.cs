using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowd.Contracts.Deployments;
using Flowd.Host;
using Flowd.Infrastructure.Postgres;
using Microsoft.AspNetCore.Builder;

namespace Flowd.Integration.Tests;

/// <summary>T-DEP-01..06, FR-DEP-001..003, FR-DEP-005, API-001, API-002 against a real PostgreSQL through the HTTP API.</summary>
[Trait("Category", "Integration")]
public sealed class DeploymentApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static string Process(string id, string extra = "") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="{id}" isExecutable="true">
            <bpmn:startEvent id="Start"><bpmn:outgoing>F1</bpmn:outgoing></bpmn:startEvent>
            <bpmn:serviceTask id="Task{extra}"><bpmn:extensionElements><flowd:taskDefinition type="work"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="End"/>
            <bpmn:sequenceFlow id="F1" sourceRef="Start" targetRef="Task{extra}"/>
            <bpmn:sequenceFlow id="F2" sourceRef="Task{extra}" targetRef="End"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static MultipartFormDataContent Form(params (string Name, string Xml)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, xml) in files)
        {
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(xml)), "resources", name);
        }

        return form;
    }

    private async Task<(WebApplication App, HttpClient Client)> StartAsync()
    {
        var cs = await postgres.CreateDatabaseAsync();
        new PostgresMigrationRunner(cs).Migrate();
        var app = FlowdHostBuilder.Build(["--urls", "http://127.0.0.1:0", $"--Flowd:ConnectionString={cs}", "--Flowd:Auth:Insecure=true"]);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static async Task<DeploymentResponse> DeployOkAsync(HttpClient client, params (string Name, string Xml)[] files)
    {
        using var form = Form(files);
        var response = await client.PostAsync(new Uri("/v1/deployments", UriKind.Relative), form);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DeploymentResponse>())!;
    }

    [Fact]
    public async Task T_DEP_01_valid_process_deploys_as_version_1()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        var result = await DeployOkAsync(client, ("order.bpmn", Process("order")));

        var deployed = result.Processes.ShouldHaveSingleItem();
        deployed.Definition.BpmnProcessId.ShouldBe("order");
        deployed.Definition.Version.ShouldBe(1);
        deployed.IsNewVersion.ShouldBeTrue();
        long.Parse(deployed.Definition.Key, System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task T_DEP_02_redeploying_identical_xml_returns_same_key_and_version()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        var first = await DeployOkAsync(client, ("order.bpmn", Process("order")));
        var second = await DeployOkAsync(client, ("order.bpmn", Process("order")));

        second.Processes.Single().Definition.Key.ShouldBe(first.Processes.Single().Definition.Key);
        second.Processes.Single().Definition.Version.ShouldBe(1);
        second.Processes.Single().IsNewVersion.ShouldBeFalse();
    }

    [Fact]
    public async Task T_DEP_03_changed_xml_creates_version_2_and_listing_shows_both()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        var v1 = (await DeployOkAsync(client, ("order.bpmn", Process("order")))).Processes.Single().Definition;
        var v2 = (await DeployOkAsync(client, ("order.bpmn", Process("order", extra: "2")))).Processes.Single().Definition;

        v2.Version.ShouldBe(2);
        v2.Key.ShouldNotBe(v1.Key);
        var page = await client.GetFromJsonAsync<PageDto<ProcessDefinitionDto>>(new Uri("/v1/process-definitions", UriKind.Relative));
        page!.Items.Select(i => i.Version).ShouldBe([1, 2]);
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task FR_DEP_001_listing_is_paginated_with_a_cursor()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        await DeployOkAsync(client, ("a.bpmn", Process("a")), ("b.bpmn", Process("b")), ("c.bpmn", Process("c")));

        var first = await client.GetFromJsonAsync<PageDto<ProcessDefinitionDto>>(new Uri("/v1/process-definitions?pageSize=2", UriKind.Relative));
        var second = await client.GetFromJsonAsync<PageDto<ProcessDefinitionDto>>(new Uri($"/v1/process-definitions?pageSize=2&after={first!.NextCursor}", UriKind.Relative));

        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();
        second!.Items.Select(i => i.BpmnProcessId).ShouldBe(["c"]);
        second.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task FR_DEP_005_xml_endpoint_returns_the_original_bytes()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        var xml = Process("order");
        var key = (await DeployOkAsync(client, ("order.bpmn", xml))).Processes.Single().Definition.Key;

        var response = await client.GetAsync(new Uri($"/v1/process-definitions/{key}/xml", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(xml);
        (await client.GetAsync(new Uri("/v1/process-definitions/999/xml", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(new Uri($"/v1/process-definitions/{key}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task T_DEP_04_unsupported_element_returns_400_with_element_id_and_code()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        var xml = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "testdata", "complex-gateway.bpmn"));

        using var form = Form(("complex.bpmn", xml));
        var response = await client.PostAsync(new Uri("/v1/deployments", UriKind.Relative), form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("code").GetString().ShouldBe("DEPLOYMENT_INVALID");
        var error = problem.RootElement.GetProperty("errors").EnumerateArray().Single();
        error.GetProperty("target").GetString().ShouldBe("complex.bpmn#Complex");
        error.GetProperty("code").GetString().ShouldBe("UNSUPPORTED_ELEMENT");
    }

    [Fact]
    public async Task T_DEP_05_all_errors_are_collected_in_one_response_and_nothing_is_stored()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        var broken = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "testdata", "dangling-and-more.bpmn"));

        using var form = Form(("ok.bpmn", Process("ok")), ("broken.bpmn", broken));
        var response = await client.PostAsync(new Uri("/v1/deployments", UriKind.Relative), form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").GetArrayLength().ShouldBeGreaterThanOrEqualTo(3);
        var page = await client.GetFromJsonAsync<PageDto<ProcessDefinitionDto>>(new Uri("/v1/process-definitions", UriKind.Relative));
        page!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task T_DEP_06_external_entity_is_rejected_with_400()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        var xxe = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "testdata", "xxe.bpmn"));

        using var form = Form(("xxe.bpmn", xxe));
        var response = await client.PostAsync(new Uri("/v1/deployments", UriKind.Relative), form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("DTD_NOT_ALLOWED");
    }

    [Fact]
    public async Task FR_DEP_001_non_multipart_request_is_rejected()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync(new Uri("/v1/deployments", UriKind.Relative), content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
