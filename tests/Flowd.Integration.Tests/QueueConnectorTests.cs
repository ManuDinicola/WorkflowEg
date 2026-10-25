using System.Text;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Flowd.Integration.Tests;

/// <summary>FR-CON-013: a service task with the RabbitMQ connector publishes to a real broker.</summary>
[Trait("Category", "Integration")]
public sealed class QueueConnectorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="q" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="Send"><bpmn:extensionElements>
              <flowd:connector type="rabbitmq" exchange="" routingKey="orders-{{region}}" body='{"id":{{id}}}' resultVariable="sent"/>
            </bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Send"/><bpmn:sequenceFlow id="f2" sourceRef="Send" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public async Task FR_CON_013_the_connector_publishes_the_templated_message_and_the_process_continues()
    {
        await using var broker = new RabbitMqBuilder("rabbitmq:3.13").Build();
        await broker.StartAsync();
        var address = broker.GetConnectionString();
        await using var app = await TestApp.StartAsync(postgres, secure: false, $"--Flowd:Connectors:Queue:Address={address}", "--Flowd:Connectors:Queue:AllowedExchanges:0=(default)");
        await app.DeployAsync(Process);
        var factory = new ConnectionFactory { Uri = new Uri(address) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync("orders-eu", durable: true, exclusive: false, autoDelete: false);

        var key = await app.StartInstanceAsync("q", """{"id":7,"region":"eu"}""");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while ((await app.GetInstanceAsync(key)).State != "COMPLETED")
        {
            await Task.Delay(100, timeout.Token);
        }

        var received = await channel.BasicGetAsync("orders-eu", autoAck: true, timeout.Token);
        received.ShouldNotBeNull();
        Encoding.UTF8.GetString(received.Body.ToArray()).ShouldBe("""{"id":7}""");
        received.BasicProperties.ContentType.ShouldBe("application/json");
        (await app.GetVariablesAsync(key))["sent"]!["published"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task FR_CON_013_an_exchange_that_is_not_allowed_ends_in_an_incident_not_a_publish()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false, "--Flowd:Connectors:Queue:Address=amqp://guest:guest@127.0.0.1:1/");
        await app.DeployAsync(Process);
        var key = await app.StartInstanceAsync("q", """{"id":1,"region":"eu"}""");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        System.Text.Json.Nodes.JsonNode? incidents = null;
        while (incidents is null || incidents["items"]!.AsArray().Count == 0)
        {
            await Task.Delay(200, timeout.Token);
            incidents = System.Text.Json.Nodes.JsonNode.Parse(await app.Client.GetStringAsync(new Uri($"/v1/incidents?processInstanceKey={key}", UriKind.Relative), timeout.Token));
        }

        incidents["items"]![0]!["errorMessage"]!.GetValue<string>().ShouldContain("AllowedExchanges");
    }
}
