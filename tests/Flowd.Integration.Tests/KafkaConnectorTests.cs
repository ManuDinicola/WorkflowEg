using System.Text;
using Confluent.Kafka;
using Testcontainers.Kafka;

namespace Flowd.Integration.Tests;

/// <summary>FR-CON-018: a service task with the Kafka connector writes a record to a real Kafka broker.</summary>
[Trait("Category", "Integration")]
public sealed class KafkaConnectorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="k" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="Send"><bpmn:extensionElements>
              <flowd:connector type="kafka" topic="orders-{{region}}" key="{{id}}" body='{"id":{{id}}}' headers='{"source":"flowd"}' resultVariable="sent"/>
            </bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Send"/><bpmn:sequenceFlow id="f2" sourceRef="Send" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public async Task FR_CON_018_the_connector_writes_the_templated_record_and_the_process_continues()
    {
        await using var broker = new KafkaBuilder("confluentinc/cp-kafka:7.5.12").Build();
        await broker.StartAsync();
        var servers = broker.GetBootstrapAddress();
        await using var app = await TestApp.StartAsync(postgres, secure: false, $"--Flowd:Connectors:Kafka:BootstrapServers={servers}", "--Flowd:Connectors:Kafka:AllowedTopics:0=orders-eu");
        await app.DeployAsync(Process);

        var key = await app.StartInstanceAsync("k", """{"id":7,"region":"eu"}""");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while ((await app.GetInstanceAsync(key)).State != "COMPLETED")
        {
            await Task.Delay(200, timeout.Token);
        }

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig { BootstrapServers = servers, GroupId = "drill", AutoOffsetReset = AutoOffsetReset.Earliest }).Build();
        consumer.Subscribe("orders-eu");
        var record = consumer.Consume(TimeSpan.FromSeconds(60));
        record.ShouldNotBeNull();
        record.Message.Key.ShouldBe("7");
        record.Message.Value.ShouldBe("""{"id":7}""");
        Encoding.UTF8.GetString(record.Message.Headers.GetLastBytes("source")).ShouldBe("flowd");
        (await app.GetVariablesAsync(key))["sent"]!["offset"]!.GetValue<long>().ShouldBe(0);
    }

    [Fact]
    public async Task FR_CON_018_a_topic_that_is_not_allowed_ends_in_an_incident_not_a_write()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false, "--Flowd:Connectors:Kafka:BootstrapServers=127.0.0.1:1", "--Flowd:Connectors:Kafka:AllowedTopics:0=other");
        await app.DeployAsync(Process);
        var key = await app.StartInstanceAsync("k", """{"id":1,"region":"eu"}""");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        System.Text.Json.Nodes.JsonNode? incidents = null;
        while (incidents is null || incidents["items"]!.AsArray().Count == 0)
        {
            await Task.Delay(200, timeout.Token);
            incidents = System.Text.Json.Nodes.JsonNode.Parse(await app.Client.GetStringAsync(new Uri($"/v1/incidents?processInstanceKey={key}", UriKind.Relative), timeout.Token));
        }

        incidents["items"]![0]!["errorMessage"]!.GetValue<string>().ShouldContain("AllowedTopics");
    }
}
