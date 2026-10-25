using Flowd.Application.Execution;
using Flowd.Application.Jobs;
using Flowd.Engine.Commands;
using Flowd.Engine.Results;

namespace Flowd.Application.Connectors;

/// <summary>
/// The internal worker of connector tasks: activates the jobs of every built-in connector (HTTP, e-mail, message queue) for every tenant, runs the
/// connector and completes or fails the job (FR-CON-010, FR-CON-012, FR-CON-013). Like every worker it is at-least-once: make the called service idempotent.
/// </summary>
public sealed class ConnectorJobProcessor(IJobQueue queue, InstanceCommandService commands, IEnumerable<IConnectorHandler> handlers, TimeProvider time)
{
    public const string WorkerName = "flowd-connectors";
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<IConnectorHandler> _handlers = [.. handlers];

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var handled = 0;
        foreach (var handler in _handlers)
        {
            handled += await RunHandlerAsync(handler, cancellationToken).ConfigureAwait(false);
        }

        return handled;
    }

    private async Task<int> RunHandlerAsync(IConnectorHandler handler, CancellationToken cancellationToken)
    {
        var handled = 0;
        var tenants = await queue.TenantsWithActivatableAsync(handler.TaskType, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        foreach (var tenant in tenants)
        {
            var request = new ActivateJobsRequest(tenant, handler.TaskType, WorkerName, JobTimeout, 20, null);
            foreach (var job in await queue.ActivateAsync(request, time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            {
                await CompleteOrFailAsync(tenant, job, await handler.ExecuteAsync(job, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                handled++;
            }
        }

        return handled;
    }

    private async Task CompleteOrFailAsync(string tenant, ActivatedJob job, ConnectorResult result, CancellationToken cancellationToken)
    {
        if (result.Success)
        {
            await commands.CompleteJobAsync(tenant, new CompleteJob(job.Key, result.Variables, WorkerName), cancellationToken).ConfigureAwait(false);
            return;
        }

        await commands.FailJobAsync(tenant, new FailJob(job.Key, Math.Max(job.Retries - 1, 0), result.Message, TimeSpan.FromSeconds(5), WorkerName), cancellationToken).ConfigureAwait(false);
    }
}
