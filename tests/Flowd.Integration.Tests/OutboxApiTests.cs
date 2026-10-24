using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Flowd.Infrastructure.Background;
using Flowd.Infrastructure.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>FR-HIS-002 and FR-ARCH-005: the transactional outbox and signed webhooks.</summary>
[Trait("Category", "Integration")]
public sealed class OutboxApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Body, string Signature, string EventId, string EventType)> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((body, request.Headers.GetValues(OutboxPublisher.SignatureHeader).Single(), request.Headers.GetValues(OutboxPublisher.EventIdHeader).Single(), request.Headers.GetValues(OutboxPublisher.EventTypeHeader).Single()));
            return new HttpResponseMessage(Status);
        }
    }

    private static OutboxPublisher Publisher(string connectionString, RecordingHandler handler, NpgsqlDataSource dataSource) =>
        new(
            dataSource,
            new HttpClient(handler),
            Options.Create(new FlowdOptions { ConnectionString = connectionString, WebhookUrl = new Uri("http://hook.test/events"), WebhookSecret = "s3cret" }),
            TimeProvider.System,
            NullLogger<OutboxPublisher>.Instance);

    [Fact]
    public async Task FR_HIS_002_lifecycle_events_are_delivered_signed_in_order_with_unique_event_ids()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false, "--Flowd:OutboxEnabled=true");
        await app.DeployAsync(Process);
        var instance = await app.StartInstanceAsync("p");
        var job = (await app.ActivateAsync("work")).Single();
        await app.CompleteAsync(job.Key);
        await using var dataSource = NpgsqlDataSource.Create(app.ConnectionString);
        var handler = new RecordingHandler();
        var publisher = Publisher(app.ConnectionString, handler, dataSource);

        var delivered = await publisher.PublishBatchAsync(CancellationToken.None);

        delivered.ShouldBe(handler.Requests.Count);
        handler.Requests.Select(r => r.EventType).ShouldBe(["INSTANCE_STARTED", "JOB_CREATED", "JOB_COMPLETED", "INSTANCE_COMPLETED"]);
        handler.Requests.Select(r => r.EventId).Distinct().Count().ShouldBe(handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            request.Signature.ShouldBe(OutboxPublisher.Sign("s3cret", System.Text.Encoding.UTF8.GetBytes(request.Body)));
            var body = JsonNode.Parse(request.Body)!.AsObject();
            body["processInstanceKey"]!.GetValue<string>().ShouldBe(instance);
            body["eventId"]!.GetValue<string>().ShouldBe(request.EventId);
        }

        (await publisher.PublishBatchAsync(CancellationToken.None)).ShouldBe(0);
        await using var connection = await dataSource.OpenConnectionAsync();
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM outbox WHERE published_at IS NULL")).ShouldBe(0);
    }

    [Fact]
    public async Task FR_HIS_002_a_failing_webhook_keeps_events_pending_and_retries_them_later()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false, "--Flowd:OutboxEnabled=true");
        await app.DeployAsync(Process);
        await app.StartInstanceAsync("p");
        await using var dataSource = NpgsqlDataSource.Create(app.ConnectionString);
        var handler = new RecordingHandler { Status = HttpStatusCode.InternalServerError };
        var publisher = Publisher(app.ConnectionString, handler, dataSource);

        (await publisher.PublishBatchAsync(CancellationToken.None)).ShouldBe(0);

        handler.Requests.Count.ShouldBe(1); // stops at the first failure to keep the order
        await using var connection = await dataSource.OpenConnectionAsync();
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM outbox WHERE published_at IS NULL AND attempts = 1 AND last_error = 'HTTP 500'")).ShouldBe(1);
        handler.Status = HttpStatusCode.OK;
        handler.Requests.Clear();
        (await publisher.PublishBatchAsync(CancellationToken.None)).ShouldBeGreaterThan(0);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM outbox WHERE published_at IS NULL")).ShouldBe(0);
    }

    [Fact]
    public async Task FR_ARCH_005_without_the_outbox_enabled_no_rows_are_written()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        await app.StartInstanceAsync("p");
        await using var dataSource = NpgsqlDataSource.Create(app.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();

        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM outbox")).ShouldBe(0);
    }
}
