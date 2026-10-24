using Flowd.Engine.State;

namespace Flowd.Application.Timers;

/// <summary>A due timer claimed by this node (FR-TMR-002).</summary>
public sealed record TimerLease(
    long Key,
    long? InstanceKey,
    long? DefinitionKey,
    string ElementId,
    string TenantId,
    TriggerKind Kind,
    DateTimeOffset DueAt,
    int? Repetitions,
    TimeSpan? Interval);

/// <summary>Port: timer scheduling store. Several nodes may call it concurrently (FR-ARCH-004).</summary>
public interface ITimerQueue
{
    /// <summary>
    /// Claims up to <paramref name="limit"/> active timers that are due and not leased by another node, oldest due first.
    /// A lease expires after <paramref name="leaseFor"/> so a crashed node does not block timers (FR-TMR-003).
    /// </summary>
    Task<IReadOnlyList<TimerLease>> LeaseDueAsync(DateTimeOffset now, int limit, TimeSpan leaseFor, CancellationToken cancellationToken);

    /// <summary>Moves a definition-level (start) timer to its next firing, or finishes it when <paramref name="nextDue"/> is null.</summary>
    Task AdvanceStartTimerAsync(long timerKey, DateTimeOffset? nextDue, int? repetitions, CancellationToken cancellationToken);
}

/// <summary>A start event trigger of a deployed definition (FR-TMR-005, FR-MSG-004).</summary>
public sealed record StartTimerDraft(long Key, string ElementId, DateTimeOffset DueAt, int? Repetitions, TimeSpan? Interval);

public sealed record StartMessageDraft(long Key, string ElementId, string MessageName);

/// <summary>Port: timers and message subscriptions that belong to a definition, not to an instance.</summary>
public interface IStartTriggerStore
{
    /// <summary>Replaces the start triggers of every earlier version of the process with those of the new version.</summary>
    Task ReplaceAsync(
        string tenantId,
        string bpmnProcessId,
        long definitionKey,
        IReadOnlyList<StartTimerDraft> timers,
        IReadOnlyList<StartMessageDraft> messages,
        CancellationToken cancellationToken);
}
