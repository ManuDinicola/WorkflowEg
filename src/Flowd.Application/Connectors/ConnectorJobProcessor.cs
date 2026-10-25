using Flowd.Application.Execution;
using Flowd.Application.Jobs;
using Flowd.Bpmn.Model;
using Flowd.Engine.Commands;
using Flowd.Engine.Results;

namespace Flowd.Application.Connectors;

/// <summary>
/// The internal worker of connector tasks: activates the HTTP and e-mail connector jobs of every tenant, runs the
/// connector and completes or fails the job (FR-CON-010, FR-CON-012). Like every worker it is at-least-once: make the called service idempotent.
/// </summary>
public sealed class ConnectorJobProcessor(IJobQueue queue, InstanceCommandService commands, HttpConnectorHandler http, EmailConnectorHandler email, TimeProvider time)
{
    public const string WorkerName = "flowd-connectors";
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] Types = [ServiceTask.HttpConnectorTaskType, ServiceTask.EmailConnectorTaskType];

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var handled = 0;
        foreach (var type in Types)
        {
            handled += await RunTypeAsync(type, cancellationToken).ConfigureAwait(false);
        }

        return handled;
    }

    private async Task<int> RunTypeAsync(string type, CancellationToken cancellationToken)
    {
        var handled = 0;
        var tenants = await queue.TenantsWithActivatableAsync(type, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        foreach (var tenant in tenants)
        {
            var request = new ActivateJobsRequest(tenant, type, WorkerName, JobTimeout, 20, null);
            foreach (var job in await queue.ActivateAsync(request, time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            {
                await HandleAsync(tenant, job, cancellationToken).ConfigureAwait(false);
                handled++;
            }
        }

        return handled;
    }

    private async Task HandleAsync(string tenant, ActivatedJob job, CancellationToken cancellationToken)
    {
        var result = string.Equals(job.Type, ServiceTask.EmailConnectorTaskType, StringComparison.Ordinal)
            ? await email.ExecuteAsync(job, cancellationToken).ConfigureAwait(false)
            : await http.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            await commands.CompleteJobAsync(tenant, new CompleteJob(job.Key, result.Variables, WorkerName), cancellationToken).ConfigureAwait(false);
            return;
        }

        await commands.FailJobAsync(tenant, new FailJob(job.Key, Math.Max(job.Retries - 1, 0), result.Message, TimeSpan.FromSeconds(5), WorkerName), cancellationToken).ConfigureAwait(false);
    }
}
