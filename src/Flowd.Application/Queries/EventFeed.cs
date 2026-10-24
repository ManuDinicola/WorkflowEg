using System.Text.Json.Nodes;

namespace Flowd.Application.Queries;

/// <summary>One lifecycle event of any instance of a tenant, in global order (FR-API-010).</summary>
public sealed record FeedEvent(long Seq, long InstanceKey, string EventType, string? ElementId, JsonObject Payload, DateTimeOffset OccurredAt);

/// <summary>Port: reads the history of all instances of a tenant in order, for the event stream.</summary>
public interface IEventFeed
{
    Task<IReadOnlyList<FeedEvent>> ReadAsync(string tenantId, long afterSeq, int take, CancellationToken cancellationToken);
}
