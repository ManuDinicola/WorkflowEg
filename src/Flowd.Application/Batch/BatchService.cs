using Flowd.Application.Execution;
using Flowd.Application.Migration;
using Flowd.Application.Queries;
using Flowd.Engine.Results;

namespace Flowd.Application.Batch;

/// <summary>Outcome of a batch run: how many items matched, succeeded and failed (the first failures are listed by key).</summary>
public sealed record BatchResult(int Matched, int Succeeded, int Failed, IReadOnlyList<long> FailedKeys, bool Truncated);

/// <summary>
/// Operator batch operations (FR-OPS-003): runs the single-item command for every match, one at a time, up to <see cref="MaxItems"/>.
/// Items are independent: one failure never stops the batch. Run it again for the rest when <see cref="BatchResult.Truncated"/> is true.
/// </summary>
public sealed class BatchService(IInstanceQueries queries, InstanceCommandService commands, MigrateInstanceUseCase migration)
{
    public const int MaxItems = 1000;
    private const int PageSize = 100;
    private const int MaxReportedFailures = 20;

    /// <summary>Cancels the active instances of the tenant that match the filter (the state is forced to ACTIVE).</summary>
    public async Task<BatchResult> CancelInstancesAsync(string tenantId, string? bpmnProcessId, int? maxItems, CancellationToken cancellationToken)
    {
        var filter = new InstanceFilter("ACTIVE", bpmnProcessId, null, null, null);
        var limit = Math.Clamp(maxItems ?? MaxItems, 1, MaxItems);
        var keys = new List<long>();
        long? after = null;
        InstancePage page;
        do
        {
            page = await queries.ListInstancesAsync(tenantId, filter, Math.Min(PageSize, limit - keys.Count), after, cancellationToken).ConfigureAwait(false);
            keys.AddRange(page.Items.Select(i => i.Key));
            after = page.NextCursor;
        }
        while (page.NextCursor is not null && keys.Count < limit);

        return await RunAsync(keys, page.NextCursor is not null, async key => await commands.CancelAsync(tenantId, key, cancellationToken).ConfigureAwait(false) is not Failure).ConfigureAwait(false);
    }

    /// <summary>Migrates the active instances of a process that are not on the target version yet (FR-OPS-004). Instances that cannot be migrated are reported as failed.</summary>
    public async Task<BatchResult> MigrateInstancesAsync(string tenantId, string bpmnProcessId, long targetDefinitionKey, IReadOnlyDictionary<string, string>? mapping, int? maxItems, CancellationToken cancellationToken)
    {
        var filter = new InstanceFilter("ACTIVE", bpmnProcessId, null, null, null);
        var limit = Math.Clamp(maxItems ?? MaxItems, 1, MaxItems);
        var keys = new List<long>();
        long? after = null;
        var more = false;
        do
        {
            var page = await queries.ListInstancesAsync(tenantId, filter, PageSize, after, cancellationToken).ConfigureAwait(false);
            keys.AddRange(page.Items.Where(i => i.DefinitionKey != targetDefinitionKey).Select(i => i.Key));
            after = page.NextCursor;
            more = page.NextCursor is not null;
        }
        while (more && keys.Count < limit);

        var truncated = more || keys.Count > limit;
        return await RunAsync([.. keys.Take(limit)], truncated, async key => await migration.ExecuteAsync(tenantId, key, targetDefinitionKey, mapping, cancellationToken).ConfigureAwait(false) is not Failure).ConfigureAwait(false);
    }

    /// <summary>Resolves the open incidents of the tenant (optionally of one instance) so their jobs or steps are retried.</summary>
    public async Task<BatchResult> ResolveIncidentsAsync(string tenantId, long? instanceKey, int? maxItems, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxItems ?? MaxItems, 1, MaxItems);
        var keys = new List<long>();
        long? after = null;
        IncidentPage page;
        do
        {
            page = await queries.ListIncidentsAsync(tenantId, instanceKey, "OPEN", Math.Min(PageSize, limit - keys.Count), after, cancellationToken).ConfigureAwait(false);
            keys.AddRange(page.Items.Select(i => i.Key));
            after = page.NextCursor;
        }
        while (page.NextCursor is not null && keys.Count < limit);

        return await RunAsync(keys, page.NextCursor is not null, async key => await commands.ResolveIncidentAsync(tenantId, key, cancellationToken).ConfigureAwait(false) is not Failure).ConfigureAwait(false);
    }

    private static async Task<BatchResult> RunAsync(List<long> keys, bool more, Func<long, Task<bool>> succeeded)
    {
        var failed = new List<long>();
        foreach (var key in keys)
        {
            if (!await succeeded(key).ConfigureAwait(false))
            {
                failed.Add(key);
            }
        }

        return new BatchResult(keys.Count, keys.Count - failed.Count, failed.Count, [.. failed.Take(MaxReportedFailures)], more);
    }
}
