using System.Text.Json.Nodes;
using Flowd.Application.Execution;
using Flowd.Application.Jobs;
using Flowd.Bpmn.Model;
using Flowd.Engine.Commands;
using Flowd.Engine.Results;

namespace Flowd.Application.Decisions;

/// <summary>
/// The internal worker of business rule tasks that call a decision: it activates the <see cref="ServiceTask.DecisionTaskType"/> jobs of every
/// tenant, evaluates the decision with the job variables, and completes the job with the result variable (FR-DMN-004).
/// </summary>
public sealed class DecisionJobProcessor(IJobQueue queue, EvaluateDecisionUseCase evaluate, InstanceCommandService commands, TimeProvider time)
{
    public const string WorkerName = "flowd-decisions";
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Processes the jobs waiting right now. Returns how many were handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var handled = 0;
        var tenants = await queue.TenantsWithActivatableAsync(ServiceTask.DecisionTaskType, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        foreach (var tenant in tenants)
        {
            var request = new ActivateJobsRequest(tenant, ServiceTask.DecisionTaskType, WorkerName, JobTimeout, 50, null);
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
        var decisionId = job.CustomHeaders.GetValueOrDefault("decisionId") ?? string.Empty;
        var resultVariable = job.CustomHeaders.GetValueOrDefault("resultVariable") ?? "decisionResult";
        var result = await evaluate.ExecuteAsync(tenant, decisionId, job.Variables, cancellationToken).ConfigureAwait(false);
        if (result is DecisionEvaluation evaluation)
        {
            var variables = new JsonObject { [resultVariable] = evaluation.Value?.DeepClone() };
            await commands.CompleteJobAsync(tenant, new CompleteJob(job.Key, variables, WorkerName), cancellationToken).ConfigureAwait(false);
            return;
        }

        var message = result is Failure { } ? $"Decision '{decisionId}' failed or was not found." : "Decision failed.";
        await commands.FailJobAsync(tenant, new FailJob(job.Key, Math.Max(job.Retries - 1, 0), message, null, WorkerName), cancellationToken).ConfigureAwait(false);
    }
}
