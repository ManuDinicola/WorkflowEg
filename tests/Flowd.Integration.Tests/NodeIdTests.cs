using Flowd.Host;
using Microsoft.AspNetCore.Builder;

namespace Flowd.Integration.Tests;

/// <summary>FR-DOM-002: two running nodes may not share a node id on one database.</summary>
[Trait("Category", "Integration")]
public sealed class NodeIdTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static WebApplication SecondNode(TestApp first, int nodeId) => FlowdHostBuilder.Build(
        ["--urls", "http://127.0.0.1:0", $"--Flowd:ConnectionString={first.ConnectionString}", "--Flowd:Auth:Insecure=true", $"--Flowd:NodeId={nodeId}", "--Flowd:NodeIdWaitSeconds=1"]);

    [Fact]
    public async Task FR_DOM_002_a_second_node_with_the_same_id_on_the_same_database_refuses_to_start()
    {
        await using var first = await TestApp.StartAsync(postgres);

        var refused = Should.Throw<InvalidOperationException>(() => SecondNode(first, nodeId: 0));

        refused.Message.ShouldContain("Node id 0 is already used");
        refused.Message.ShouldContain("Flowd__NodeId");
    }

    [Fact]
    public async Task FR_DOM_002_a_node_with_another_id_starts_and_the_id_is_free_again_after_the_first_node_stops()
    {
        var first = await TestApp.StartAsync(postgres);
        await using (var other = SecondNode(first, nodeId: 1))
        {
            await other.StartAsync();
            await other.StopAsync();
        }

        var connectionString = first.ConnectionString;
        await first.DisposeAsync();

        await using var again = FlowdHostBuilder.Build(["--urls", "http://127.0.0.1:0", $"--Flowd:ConnectionString={connectionString}", "--Flowd:Auth:Insecure=true", "--Flowd:NodeId=0", "--Flowd:NodeIdWaitSeconds=10"]);
        await again.StartAsync();
        await again.StopAsync();
    }
}
