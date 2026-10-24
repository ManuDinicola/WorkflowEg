using Dapper;
using Flowd.Infrastructure.Background;
using Flowd.Infrastructure.Postgres.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>FR-DB-002: finished instances and old history are purged in batches; running instances are never touched.</summary>
[Trait("Category", "Integration")]
public sealed class RetentionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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

    [Fact]
    public async Task FR_DB_002_the_janitor_purges_finished_instances_after_the_retention_and_keeps_running_ones()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false, "--Flowd:OutboxEnabled=true");
        await app.DeployAsync(Process);
        var finished = await app.StartInstanceAsync("p", "{\"a\":1}");
        await app.CompleteAsync((await app.ActivateAsync("work")).Single().Key);
        var running = await app.StartInstanceAsync("p", "{\"b\":2}");
        await using var dataSource = NpgsqlDataSource.Create(app.ConnectionString);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var janitor = new Janitor(new PostgresRetentionStore(dataSource), Options.Create(new RetentionOptions { BatchSize = 100 }), clock, NullLogger<Janitor>.Instance);

        (await janitor.RunOnceAsync(CancellationToken.None)).Total.ShouldBe(0);
        clock.Advance(TimeSpan.FromDays(31));
        var result = await janitor.RunOnceAsync(CancellationToken.None);

        result.Instances.ShouldBe(1);
        result.HistoryEvents.ShouldBeGreaterThan(0);
        await using var connection = await dataSource.OpenConnectionAsync();
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM process_instance WHERE key = @k", new { k = long.Parse(finished, System.Globalization.CultureInfo.InvariantCulture) })).ShouldBe(0);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM variable WHERE instance_key = @k", new { k = long.Parse(finished, System.Globalization.CultureInfo.InvariantCulture) })).ShouldBe(0);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM job WHERE instance_key = @k", new { k = long.Parse(finished, System.Globalization.CultureInfo.InvariantCulture) })).ShouldBe(0);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM process_instance WHERE key = @k", new { k = long.Parse(running, System.Globalization.CultureInfo.InvariantCulture) })).ShouldBe(1);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM job WHERE instance_key = @k", new { k = long.Parse(running, System.Globalization.CultureInfo.InvariantCulture) })).ShouldBe(1);
    }
}
