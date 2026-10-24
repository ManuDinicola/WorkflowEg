using System.Text.Json.Nodes;
using Flowd.Application.Execution;
using Flowd.Engine.Commands;
using Flowd.Engine.Keys;
using Flowd.Engine.Results;
using Flowd.Engine.State;

namespace Flowd.Application.Messaging;

/// <summary>Request to publish a message (FR-MSG-001).</summary>
public sealed record PublishMessageRequest(string TenantId, string Name, string CorrelationKey, JsonObject? Variables, TimeSpan Ttl, string? MessageId);

public sealed record PublishedMessage(long Key, int Correlations);

/// <summary>
/// Publishes messages: buffers them for their TTL, delivers them to waiting subscriptions (at most one instance per definition)
/// and starts instances for message start events (FR-MSG-001..004, FR-CON-002).
/// </summary>
public sealed class MessageService(IMessageStore store, CommandProcessor processor, StartInstanceUseCase starter, IKeyGenerator keys, TimeProvider time)
{
    public async Task<Result<PublishedMessage>> PublishAsync(PublishMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Ttl < TimeSpan.Zero)
        {
            return new Failure(new ValidationFailure("A message needs a name and a non-negative ttl."));
        }

        var now = time.GetUtcNow();
        var message = new BufferedMessage(
            keys.NextKey(),
            request.TenantId,
            request.Name,
            request.CorrelationKey ?? string.Empty,
            string.IsNullOrEmpty(request.MessageId) ? null : request.MessageId,
            request.Variables ?? [],
            now + request.Ttl,
            now);
        if (!await store.TryBufferAsync(message, cancellationToken).ConfigureAwait(false))
        {
            return new Failure(new Conflict("MESSAGE_ALREADY_EXISTS", "A message with this id is already buffered."));
        }

        var correlations = await CorrelateWaitingAsync(message, cancellationToken).ConfigureAwait(false);
        correlations += await StartInstancesAsync(message, cancellationToken).ConfigureAwait(false);
        return new PublishedMessage(message.Key, correlations);
    }

    private async Task<int> CorrelateWaitingAsync(BufferedMessage message, CancellationToken cancellationToken)
    {
        var subscriptions = await store.FindSubscriptionsAsync(message.TenantId, message.Name, message.CorrelationKey, cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var definition in subscriptions.GroupBy(s => s.DefinitionKey))
        {
            if (await store.IsCorrelatedAsync(message.Key, definition.Key, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            foreach (var subscription in definition.Where(s => s.InstanceKey is not null).OrderBy(s => s.Key))
            {
                var command = new CorrelateMessage(subscription.Key, message.Key, message.Name, message.Variables);
                if (await processor.ExecuteAsync(message.TenantId, subscription.InstanceKey!.Value, command, cancellationToken).ConfigureAwait(false) is ProcessedCommand)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    private async Task<int> StartInstancesAsync(BufferedMessage message, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var subscription in await store.FindStartSubscriptionsAsync(message.TenantId, message.Name, cancellationToken).ConfigureAwait(false))
        {
            if (await store.IsCorrelatedAsync(message.Key, subscription.DefinitionKey, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var request = new StartInstanceRequest(message.TenantId, string.Empty, null, message.Variables, null, subscription.ElementId, subscription.DefinitionKey, message.Key);
            if (await starter.ExecuteAsync(request, cancellationToken).ConfigureAwait(false) is StartedInstance)
            {
                count++;
            }
        }

        return count;
    }
}
