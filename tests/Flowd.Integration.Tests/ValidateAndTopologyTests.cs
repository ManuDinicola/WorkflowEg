using System.Text;
using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>FR-DEP-008: validate without deploying. FR-OPS-011: topology.</summary>
[Trait("Category", "Integration")]
public sealed class ValidateAndTopologyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Good = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="ok" isExecutable="true"><bpmn:startEvent id="S"/><bpmn:endEvent id="E"/><bpmn:sequenceFlow id="f" sourceRef="S" targetRef="E"/></bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<JsonNode> ValidateAsync(TestApp app, string name, string xml)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(xml)), "resources", name } };
        var response = await app.Client.PostAsync(new Uri("/v1/deployments/validate", UriKind.Relative), form);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task FR_DEP_008_validation_reports_every_problem_and_deploys_nothing()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var bad = Good.Replace("<bpmn:endEvent id=\"E\"/>", "<bpmn:endEvent id=\"E\"/><bpmn:serviceTask id=\"T\"/>", StringComparison.Ordinal);

        var ok = await ValidateAsync(app, "ok.bpmn", Good);
        var broken = await ValidateAsync(app, "bad.bpmn", bad);
        var garbage = await ValidateAsync(app, "x.bpmn", "<definitions");

        ok["valid"]!.GetValue<bool>().ShouldBeTrue();
        broken["valid"]!.GetValue<bool>().ShouldBeFalse();
        broken["problems"]!.AsArray().Count.ShouldBeGreaterThan(0);
        garbage["valid"]!.GetValue<bool>().ShouldBeFalse();
        JsonNode.Parse(await app.Client.GetStringAsync(new Uri("/v1/process-definitions", UriKind.Relative)))!["items"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task FR_OPS_011_topology_lists_the_running_nodes()
    {
        await using var app = await TestApp.StartAsync(postgres);

        var topology = JsonNode.Parse(await app.Client.GetStringAsync(new Uri("/v1/topology", UriKind.Relative)))!;

        topology["nodeCount"]!.GetValue<int>().ShouldBe(1);
        topology["nodes"]![0]!["nodeId"]!.GetValue<int>().ShouldBe(0);
        topology["version"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }
}
