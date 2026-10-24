using Flowd.Application.Execution;
using Flowd.Engine.Commands;
using Flowd.Engine.State;

namespace Flowd.Application.Timers;

/// <summary>Fires due timers, each as its own command or instance start (FR-TMR-002, FR-TMR-003, FR-TMR-006).</summary>
public sealed class TimerService(ITimerQueue queue, CommandProcessor processor, StartInstanceUseCase starter, TimeProvider time)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private const int BatchSize = 100;

    /// <summary>Fires everything that is due now. Returns how many timers were handled.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var leases = await queue.LeaseDueAsync(now, BatchSize, LeaseDuration, cancellationToken).ConfigureAwait(false);
        foreach (var lease in leases)
        {
            if (lease.Kind == TriggerKind.Start)
            {
                await FireStartAsync(lease, cancellationToken).ConfigureAwait(false);
            }
            else if (lease.InstanceKey is { } instanceKey)
            {
                // A conflict means another node or an earlier attempt already handled the timer (exactly-once, T-TMR-05).
                await processor.ExecuteAsync(lease.TenantId, instanceKey, new TriggerTimer(lease.Key), cancellationToken).ConfigureAwait(false);
            }
        }

        return leases.Count;
    }

    private async Task FireStartAsync(TimerLease lease, CancellationToken cancellationToken)
    {
        if (lease.DefinitionKey is not { } definitionKey)
        {
            return;
        }

        var request = new StartInstanceRequest(lease.TenantId, string.Empty, null, null, null, lease.ElementId, definitionKey);
        if (await starter.ExecuteAsync(request, cancellationToken).ConfigureAwait(false) is not StartedInstance)
        {
            return; // stays leased until the lease expires, then it is retried
        }

        if (lease.Interval is { } interval && lease.Repetitions is null or > 1)
        {
            await queue.AdvanceStartTimerAsync(lease.Key, lease.DueAt + interval, lease.Repetitions is { } left ? left - 1 : null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await queue.AdvanceStartTimerAsync(lease.Key, null, null, cancellationToken).ConfigureAwait(false);
        }
    }
}
