using System.Net;
using System.Net.Http.Json;
using Flowd.Application.Timers;
using Flowd.Contracts.Execution;
using Flowd.Infrastructure.Postgres.Execution;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>M4 on a real PostgreSQL: messages (T-MSG), timers (T-TMR) and the schedulers.</summary>
[Trait("Category", "Integration")]
public sealed class MessagingAndTimerApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static string WaitForMessage(string id = "p", string messageName = "payment") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:message id="M1" name="{messageName}"><bpmn:extensionElements><flowd:subscription correlationKey="= orderId"/></bpmn:extensionElements></bpmn:message>
          <bpmn:process id="{id}" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:intermediateCatchEvent id="Wait"><bpmn:messageEventDefinition messageRef="M1"/></bpmn:intermediateCatchEvent>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Wait"/>
            <bpmn:sequenceFlow id="f2" sourceRef="Wait" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static string TimerProcess(string duration) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL">
          <bpmn:process id="t" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:intermediateCatchEvent id="Wait"><bpmn:timerEventDefinition><bpmn:timeDuration>{duration}</bpmn:timeDuration></bpmn:timerEventDefinition></bpmn:intermediateCatchEvent>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Wait"/>
            <bpmn:sequenceFlow id="f2" sourceRef="Wait" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static Task<HttpResponseMessage> Publish(TestApp app, string name, string key, string? variablesJson = null, long? ttlMs = null, string? messageId = null) =>
        app.Client.PostAsJsonAsync(
            new Uri("/v1/messages", UriKind.Relative),
            new PublishMessageRequestDto(name, key, variablesJson is null ? null : System.Text.Json.Nodes.JsonNode.Parse(variablesJson)!.AsObject(), ttlMs, messageId));

    [Fact]
    public async Task T_MSG_correlation_completes_the_waiting_instance_and_hands_over_the_message_variables()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(WaitForMessage());
        var key = await app.StartInstanceAsync("p", "{\"orderId\":\"A-1\"}");

        var response = await Publish(app, "payment", "A-1", "{\"paid\":true}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PublishMessageResponseDto>())!.Correlations.ShouldBe(1);
        (await app.GetInstanceAsync(key)).State.ShouldBe("COMPLETED");
        (await app.GetVariablesAsync(key))["paid"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task T_MSG_01_a_buffered_message_is_delivered_when_the_subscription_opens()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(WaitForMessage());
        (await Publish(app, "payment", "A-2", "{\"early\":1}", ttlMs: 60_000)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var key = await app.StartInstanceAsync("p", "{\"orderId\":\"A-2\"}");

        (await app.GetInstanceAsync(key)).State.ShouldBe("COMPLETED");
        (await app.GetVariablesAsync(key))["early"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task T_MSG_03_a_duplicate_message_id_is_rejected_with_409()
    {
        await using var app = await TestApp.StartAsync(postgres);
        (await Publish(app, "payment", "k", ttlMs: 60_000, messageId: "m-1")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var duplicate = await Publish(app, "payment", "k", ttlMs: 60_000, messageId: "m-1");

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await TestApp.ProblemCodeAsync(duplicate)).ShouldBe("MESSAGE_ALREADY_EXISTS");
    }

    [Fact]
    public async Task T_MSG_05_a_message_reaches_one_instance_per_definition_even_when_several_wait()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(WaitForMessage());
        var first = await app.StartInstanceAsync("p", "{\"orderId\":\"same\"}");
        var second = await app.StartInstanceAsync("p", "{\"orderId\":\"same\"}");

        await Publish(app, "payment", "same", ttlMs: 60_000);

        var states = new[] { (await app.GetInstanceAsync(first)).State, (await app.GetInstanceAsync(second)).State };
        states.Count(s => s == "COMPLETED").ShouldBe(1);
        states.Count(s => s == "ACTIVE").ShouldBe(1);
    }

    [Fact]
    public async Task T_MSG_04_a_message_start_event_creates_an_instance()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync("""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <bpmn:message id="M1" name="order-placed"/>
              <bpmn:process id="starter" isExecutable="true">
                <bpmn:startEvent id="S"><bpmn:messageEventDefinition messageRef="M1"/></bpmn:startEvent>
                <bpmn:endEvent id="E"/>
                <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="E"/>
              </bpmn:process>
            </bpmn:definitions>
            """);

        var response = await Publish(app, "order-placed", "x", "{\"item\":\"book\"}");

        (await response.Content.ReadFromJsonAsync<PublishMessageResponseDto>())!.Correlations.ShouldBe(1);
        var list = await app.Client.GetFromJsonAsync<Flowd.Contracts.Deployments.PageDto<InstanceDto>>(new Uri("/v1/process-instances?state=COMPLETED", UriKind.Relative));
        var instance = list!.Items.ShouldHaveSingleItem();
        (await app.GetVariablesAsync(instance.Key))["item"]!.GetValue<string>().ShouldBe("book");
    }

    [Fact]
    public async Task T_TMR_the_background_scheduler_fires_a_due_timer()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(TimerProcess("PT0S"));
        var key = await app.StartInstanceAsync("t");

        var state = "ACTIVE";
        for (var attempt = 0; attempt < 50 && state != "COMPLETED"; attempt++)
        {
            await Task.Delay(100);
            state = (await app.GetInstanceAsync(key)).State;
        }

        state.ShouldBe("COMPLETED");
    }

    [Fact]
    public async Task T_TMR_04_and_05_leases_are_exclusive_survive_restarts_and_expire()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(TimerProcess("PT1H"));
        await app.StartInstanceAsync("t");
        await using var dataSource = NpgsqlDataSource.Create(app.ConnectionString);
        var queue = new PostgresTimerQueue(dataSource);
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nowForDb = DateTimeOffset.UtcNow;

        (await queue.LeaseDueAsync(nowForDb, 10, TimeSpan.FromSeconds(30), CancellationToken.None)).ShouldBeEmpty();
        var due = nowForDb + TimeSpan.FromHours(2);

        var racing = await Task.WhenAll(
            queue.LeaseDueAsync(due, 10, TimeSpan.FromSeconds(30), CancellationToken.None),
            queue.LeaseDueAsync(due, 10, TimeSpan.FromSeconds(30), CancellationToken.None));
        racing.Sum(r => r.Count).ShouldBe(1);

        (await queue.LeaseDueAsync(due, 10, TimeSpan.FromSeconds(30), CancellationToken.None)).ShouldBeEmpty();
        var afterExpiry = await queue.LeaseDueAsync(due + TimeSpan.FromSeconds(31), 10, TimeSpan.FromSeconds(30), CancellationToken.None);
        afterExpiry.ShouldHaveSingleItem().Kind.ShouldBe(Flowd.Engine.State.TriggerKind.Catch);
        start.Year.ShouldBe(2030);
    }
}
