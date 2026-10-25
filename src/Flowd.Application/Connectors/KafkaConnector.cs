using System.Text.Json.Nodes;
using Flowd.Application.Jobs;
using Flowd.Bpmn.Model;

namespace Flowd.Application.Connectors;

/// <summary>A record the connector wants to append to a Kafka topic.</summary>
public sealed record KafkaRecord(string Topic, string? Key, string Value, IReadOnlyDictionary<string, string> Headers);

/// <summary>Port: produces to Kafka and returns once the brokers have acknowledged the record (all in-sync replicas).</summary>
public interface IKafkaProducer
{
    /// <summary>Returns the partition and offset the record was written at.</summary>
    Task<(int Partition, long Offset)> ProduceAsync(KafkaRecord record, CancellationToken cancellationToken);
}

/// <summary>Kafka settings, bound from <c>Flowd:Connectors:Kafka</c>. Without <see cref="BootstrapServers"/> the Kafka connector fails its jobs.</summary>
public sealed class KafkaSettings
{
    /// <summary>Comma separated <c>host:port</c> list of brokers.</summary>
    public string BootstrapServers { get; init; } = string.Empty;

    /// <summary>Topics the connector may write to (exact names). Empty refuses every record; <c>*</c> allows any.</summary>
    public string[] AllowedTopics { get; init; } = [];

    /// <summary><c>Plaintext</c> (default), <c>Ssl</c>, <c>SaslPlaintext</c> or <c>SaslSsl</c>.</summary>
    public string SecurityProtocol { get; init; } = "Plaintext";

    /// <summary><c>Plain</c>, <c>ScramSha256</c> or <c>ScramSha512</c> when a SASL protocol is used.</summary>
    public string SaslMechanism { get; init; } = "Plain";

    public string SaslUsername { get; init; } = string.Empty;

    public string SaslPassword { get; init; } = string.Empty;
}

/// <summary>
/// The Kafka connector (FR-CON-018): <c>&lt;flowd:connector type="kafka" topic="orders" key="{{orderId}}" body='{"id":{{id}}}' headers='{"source":"flowd"}' resultVariable="sent"/&gt;</c>.
/// Text settings are templates. The result variable gets <c>{ partition, offset }</c>. Delivery is at least once: a retry after a crash can write the record twice.
/// </summary>
public sealed class KafkaConnectorHandler(IKafkaProducer producer, ISecretProvider secrets, ConnectorOptions options) : IConnectorHandler
{
    public string TaskType => ServiceTask.KafkaConnectorTaskType;

    public async Task<ConnectorResult> ExecuteAsync(ActivatedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            var (partition, offset) = await producer.ProduceAsync(Build(job), cancellationToken).ConfigureAwait(false);
            var resultVariable = job.CustomHeaders.GetValueOrDefault("resultVariable") ?? "kafka";
            return ConnectorResult.Ok(new JsonObject { [resultVariable] = new JsonObject { ["partition"] = partition, ["offset"] = offset } });
        }
        catch (ConnectorException ex)
        {
            return ConnectorResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return ConnectorResult.Failed($"Writing to Kafka failed: {ex.GetType().Name}.");
        }
    }

    private KafkaRecord Build(ActivatedJob job)
    {
        if (string.IsNullOrWhiteSpace(options.Kafka.BootstrapServers))
        {
            throw new ConnectorException("Flowd:Connectors:Kafka:BootstrapServers is not configured.");
        }

        var settings = job.CustomHeaders;
        var topic = Render(settings.GetValueOrDefault("topic") ?? throw new ConnectorException("The Kafka connector needs a topic."), job);
        if (!options.Kafka.AllowedTopics.Any(t => t == "*" || string.Equals(t, topic, StringComparison.Ordinal)))
        {
            throw new ConnectorException($"Topic '{topic}' is not in Flowd:Connectors:Kafka:AllowedTopics.");
        }

        return new KafkaRecord(topic, settings.TryGetValue("key", out var key) ? Render(key, job) : null, Render(settings.GetValueOrDefault("body") ?? string.Empty, job), Headers(settings.GetValueOrDefault("headers"), job));
    }

    private Dictionary<string, string> Headers(string? json, ActivatedJob job)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return headers;
        }

        try
        {
            foreach (var (name, value) in (JsonNode.Parse(Render(json, job)) as JsonObject) ?? throw new ConnectorException("The Kafka headers must be a JSON object."))
            {
                headers[name] = value?.ToString() ?? string.Empty;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ConnectorException("The Kafka headers must be a JSON object.");
        }

        return headers;
    }

    private string Render(string template, ActivatedJob job) => Template.Render(template, job.Variables, secrets);
}
