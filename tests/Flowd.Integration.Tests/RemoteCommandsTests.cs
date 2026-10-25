using System.Text.Json.Nodes;
using Flowd.Host;

namespace Flowd.Integration.Tests;

/// <summary>FR-OPS-012: <c>flowd deploy</c> and <c>flowd start</c>.</summary>
[Trait("Category", "Integration")]
public sealed class RemoteCommandsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="cli" isExecutable="true"><bpmn:startEvent id="S"/><bpmn:endEvent id="E"/><bpmn:sequenceFlow id="f" sourceRef="S" targetRef="E"/></bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public async Task FR_OPS_012_deploy_and_start_work_against_a_running_flowd_and_report_failures_with_a_failing_exit_code()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var directory = Directory.CreateTempSubdirectory("flowd-cli-");
        try
        {
            var good = new FileInfo(Path.Combine(directory.FullName, "cli.bpmn"));
            await File.WriteAllTextAsync(good.FullName, Process);
            var bad = new FileInfo(Path.Combine(directory.FullName, "bad.bpmn"));
            await File.WriteAllTextAsync(bad.FullName, "<definitions");
            using var output = new StringWriter();

            var deployed = await RemoteCommands.DeployAsync(app.Client, [good], output, CancellationToken.None);
            var started = await RemoteCommands.StartAsync(app.Client, "cli", """{"n":1}""", output, CancellationToken.None);
            var rejected = await RemoteCommands.DeployAsync(app.Client, [bad], output, CancellationToken.None);
            var unknown = await RemoteCommands.StartAsync(app.Client, "nope", null, output, CancellationToken.None);

            (deployed, started, rejected, unknown).ShouldBe((0, 0, 1, 1));
            JsonNode.Parse(await app.Client.GetStringAsync(new Uri("/v1/process-instances", UriKind.Relative)))!["items"]!.AsArray().Count.ShouldBe(1);
            output.ToString().ShouldContain("cli.bpmn: 200");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
