using System.Text.Json.Nodes;
using Flowd.Engine.State;

namespace Flowd.Application.Messaging;

/// <summary>A published message waiting for subscribers until it expires (FR-MSG-001, FR-MSG-002).</summary>
public sealed record BufferedMessage(
    long Key,
    string TenantId,
    string Name,
    string CorrelationKey,
    string? MessageId,
    JsonObject Variables,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

/// <summary>Port: message buffer and subscription lookup (section 11, section 15).</summary>
public interface IMessageStore
{
    /// <summary>Stores the message. False when the same <c>(name, correlationKey, messageId)</c> is still buffered (FR-MSG-003).</summary>
    Task<bool> TryBufferAsync(BufferedMessage message, CancellationToken cancellationToken);

    /// <summary>Open subscriptions of running instances (catch, boundary, event gateway) for a name and correlation key, oldest first.</summary>
    Task<IReadOnlyList<SubscriptionRecord>> FindSubscriptionsAsync(string tenantId, string name, string correlationKey, CancellationToken cancellationToken);

    /// <summary>Message start events of the latest deployed versions that wait for this message name.</summary>
    Task<IReadOnlyList<SubscriptionRecord>> FindStartSubscriptionsAsync(string tenantId, string name, CancellationToken cancellationToken);

    /// <summary>Unexpired buffered messages for a subscription, oldest first.</summary>
    Task<IReadOnlyList<BufferedMessage>> FindBufferedAsync(string tenantId, string name, string correlationKey, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> IsCorrelatedAsync(long messageKey, long definitionKey, CancellationToken cancellationToken);

    /// <summary>Deletes expired messages (and their correlation marks). Returns how many.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
