using System.Text.Json.Nodes;
using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>Message throw events publish a message that another process receives (FR-MSG-006).</summary>
public sealed class MessageThrowTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static async Task<TestEngine> WithReceiverAsync()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "receiver",
            MessageDef("M1", "ping", "= key"),
            Start(),
            CatchMessage("Wait", "M1"),
            End(),
            Flow("f1", "Start", "Wait"),
            Flow("f2", "Wait", "End")));
        return engine;
    }

    [Fact]
    public async Task FR_MSG_006_an_intermediate_throw_event_wakes_a_waiting_process_with_the_same_correlation_key()
    {
        var engine = await WithReceiverAsync();
        await engine.DeployAsync(ProcessWithMessages(
            "sender",
            MessageDef("M1", "ping", "= key"),
            Start(),
            ThrowMessagePublish("Send", "M1"),
            Service("After", "after"),
            End(),
            Flow("f1", "Start", "Send"),
            Flow("f2", "Send", "After"),
            Flow("f3", "After", "End")));
        var receiver = await engine.StartAsync("receiver", Json("""{"key":"k1"}"""));
        var other = await engine.StartAsync("receiver", Json("""{"key":"other"}"""));

        var sender = await engine.StartAsync("sender", Json("""{"key":"k1"}"""));

        engine.AssertCompleted(receiver);
        engine.AssertActiveElements(other, "Wait");
        engine.AssertActiveElements(sender, "After"); // the sender carried on without waiting
    }

    [Fact]
    public async Task FR_MSG_006_a_message_end_event_publishes_when_the_path_ends()
    {
        var engine = await WithReceiverAsync();
        await engine.DeployAsync(ProcessWithMessages(
            "sender",
            MessageDef("M1", "ping", "= key"),
            Start(),
            MessageEndPublish("Done", "M1"),
            Flow("f1", "Start", "Done")));
        var receiver = await engine.StartAsync("receiver", Json("""{"key":"k2"}"""));

        var sender = await engine.StartAsync("sender", Json("""{"key":"k2"}"""));

        engine.AssertCompleted(sender);
        engine.AssertCompleted(receiver);
    }

    [Fact]
    public async Task FR_MSG_006_a_message_thrown_a_moment_early_is_buffered_for_the_receiver()
    {
        var engine = await WithReceiverAsync();
        await engine.DeployAsync(ProcessWithMessages(
            "sender",
            MessageDef("M1", "ping", "= key"),
            Start(),
            ThrowMessagePublish("Send", "M1"),
            End(),
            Flow("f1", "Start", "Send"),
            Flow("f2", "Send", "End")));
        await engine.StartAsync("sender", Json("""{"key":"early"}"""));

        var receiver = await engine.StartAsync("receiver", Json("""{"key":"early"}"""));

        engine.AssertCompleted(receiver);
    }

    [Fact]
    public async Task FR_MSG_006_a_correlation_key_that_is_not_text_raises_an_incident()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(ProcessWithMessages(
            "sender",
            MessageDef("M1", "ping", "= key"),
            Start(),
            ThrowMessagePublish("Send", "M1"),
            End(),
            Flow("f1", "Start", "Send"),
            Flow("f2", "Send", "End")));

        var sender = await engine.StartAsync("sender", Json("""{"key":5}"""));

        engine.OpenIncidents(sender).ShouldNotBeEmpty();
    }
}
