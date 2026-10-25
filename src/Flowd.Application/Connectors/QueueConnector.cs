using System.Text.Json.Nodes;
using Flowd.Application.Jobs;
using Flowd.Bpmn.Model;

namespace Flowd.Application.Connectors;

/// <summary>A message the connector wants to put on a queue or exchange.</summary>
public sealed record QueueMessage(string Exchange, string RoutingKey, string ContentType, string Body);

/// <summary>Port: publishes to a message broker and returns once the broker has accepted the message (publisher confirm).</summary>
public interface IQueuePublisher
{
    Task PublishAsync(QueueMessage message, CancellationToken cancellationToken);
}

/// <summary>Broker settings, bound from <c>Flowd:Connectors:Queue</c>. Without an <see cref="Address"/> the queue connector fails its jobs.</summary>
public sealed class QueueSettings
{
    /// <summary>AMQP 0-9-1 address such as <c>amqp://user:password@host:5672/vhost</c> (RabbitMQ). Keep the password in an environment variable.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>Exchanges the connector may publish to (exact names). Empty refuses every message. Use <c>(default)</c> for the default exchange, which routes by queue name, and <c>*</c> for any.</summary>
    public string[] AllowedExchanges { get; init; } = [];
}

/// <summary>
/// The built-in message queue connector (FR-CON-013): <c>&lt;flowd:connector type="rabbitmq" exchange="orders" routingKey="new" body='{"id":{{id}}}' contentType="application/json" resultVariable="published"/&gt;</c>.
/// Text settings are templates (see <see cref="Template"/>). The result variable gets <c>{ published: true }</c>. Delivery is at least once: a retry after a crash can publish twice.
/// </summary>
public sealed class QueueConnectorHandler(IQueuePublisher publisher, ISecretProvider secrets, ConnectorOptions options) : IConnectorHandler
{
    public string TaskType => ServiceTask.QueueConnectorTaskType;

    public async Task<ConnectorResult> ExecuteAsync(ActivatedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            await publisher.PublishAsync(Build(job), cancellationToken).ConfigureAwait(false);
            var resultVariable = job.CustomHeaders.GetValueOrDefault("resultVariable") ?? "published";
            return ConnectorResult.Ok(new JsonObject { [resultVariable] = new JsonObject { ["published"] = true } });
        }
        catch (ConnectorException ex)
        {
            return ConnectorResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return ConnectorResult.Failed($"Publishing to the queue failed: {ex.GetType().Name}.");
        }
    }

    private QueueMessage Build(ActivatedJob job)
    {
        if (string.IsNullOrWhiteSpace(options.Queue.Address))
        {
            throw new ConnectorException("Flowd:Connectors:Queue:Address is not configured.");
        }

        var settings = job.CustomHeaders;
        var exchange = Render(settings.GetValueOrDefault("exchange") ?? string.Empty, job);
        var allowed = options.Queue.AllowedExchanges;
        if (!allowed.Any(a => a == "*" || string.Equals(a, exchange.Length == 0 ? "(default)" : exchange, StringComparison.Ordinal)))
        {
            throw new ConnectorException($"Exchange '{(exchange.Length == 0 ? "(default)" : exchange)}' is not in Flowd:Connectors:Queue:AllowedExchanges.");
        }

        var routingKey = Render(settings.GetValueOrDefault("routingKey") ?? string.Empty, job);
        if (exchange.Length == 0 && routingKey.Length == 0)
        {
            throw new ConnectorException("Publishing to the default exchange needs a routingKey (the queue name).");
        }

        return new QueueMessage(exchange, routingKey, settings.GetValueOrDefault("contentType") ?? "application/json", Render(settings.GetValueOrDefault("body") ?? string.Empty, job));
    }

    private string Render(string template, ActivatedJob job) => Template.Render(template, job.Variables, secrets);
}
