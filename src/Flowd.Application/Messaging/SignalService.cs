using System.Text.Json.Nodes;
using Flowd.Application.Execution;
using Flowd.Engine.Commands;
using Flowd.Engine.Results;
using Flowd.Engine.State;

namespace Flowd.Application.Messaging;

/// <summary>Port: broadcasts a signal to everything waiting for it.</summary>
public interface ISignalBroadcaster
{
    Task<int> BroadcastAsync(string tenantId, string name, JsonObject? variables, CancellationToken cancellationToken);
}

/// <summary>Port: signal subscriptions (FR-SIG-001). Signals are never buffered.</summary>
public interface ISignalStore
{
    Task<IReadOnlyList<SubscriptionRecord>> FindSignalSubscriptionsAsync(string tenantId, string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<SubscriptionRecord>> FindStartSignalSubscriptionsAsync(string tenantId, string name, CancellationToken cancellationToken);
}

/// <summary>
/// Broadcasts a signal: every matching subscription across all instances is triggered, one transaction per instance (FR-CON-002),
/// and every signal start event creates an instance (FR-SIG-001).
/// </summary>
public sealed class SignalService(ISignalStore store, CommandProcessor processor, StartInstanceUseCase starter) : ISignalBroadcaster
{
    public async Task<int> BroadcastAsync(string tenantId, string name, JsonObject? variables, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var subscription in await store.FindSignalSubscriptionsAsync(tenantId, name, cancellationToken).ConfigureAwait(false))
        {
            if (subscription.InstanceKey is { } instanceKey
                && await processor.ExecuteAsync(tenantId, instanceKey, new TriggerSignal(subscription.Key, name, variables), cancellationToken).ConfigureAwait(false) is ProcessedCommand)
            {
                count++;
            }
        }

        foreach (var start in await store.FindStartSignalSubscriptionsAsync(tenantId, name, cancellationToken).ConfigureAwait(false))
        {
            var request = new StartInstanceRequest(tenantId, string.Empty, null, variables, null, start.ElementId, start.DefinitionKey);
            if (await starter.ExecuteAsync(request, cancellationToken).ConfigureAwait(false) is StartedInstance)
            {
                count++;
            }
        }

        return count;
    }
}
