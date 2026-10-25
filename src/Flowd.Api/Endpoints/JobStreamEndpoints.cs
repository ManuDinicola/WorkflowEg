using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Flowd.Api.Errors;
using Flowd.Api.Security;
using Flowd.Application.Jobs;
using Flowd.Contracts.Execution;
using Flowd.Engine.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>
/// Job streaming (FR-JOB-010): a worker keeps one connection open and the engine pushes activated jobs as server-sent events instead of the
/// worker polling. Results are still reported with the normal REST calls. Delivery stays at-least-once: a job pushed to a connection that
/// dies is handed out again after its timeout.
/// </summary>
public static class JobStreamEndpoints
{
    private static readonly TimeSpan WaitPerRound = TimeSpan.FromSeconds(5);

    public static IEndpointRouteBuilder MapJobStreamEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/jobs/stream", StreamAsync).RequireAuthorization(Policies.Work);
        return routes;
    }

    private static async Task StreamAsync(
        HttpContext context,
        ClaimsPrincipal user,
        JobActivationService activation,
        string type,
        string worker,
        long? timeoutMs,
        int? maxJobs,
        string? fetchVariables,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(worker))
        {
            await FailureMapper.ToProblem(new Failure(new ValidationFailure("type and worker are required."))).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var request = new ActivateJobsRequest(
            user.TenantId(),
            type,
            worker,
            TimeSpan.FromMilliseconds(Math.Max(timeoutMs ?? 30_000, 1)),
            Math.Clamp(maxJobs ?? 10, 1, 100),
            string.IsNullOrWhiteSpace(fetchVariables) ? null : fetchVariables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var jobs = await activation.ActivateAsync(request, WaitPerRound, cancellationToken).ConfigureAwait(false);
                foreach (var job in jobs)
                {
                    var dto = new JobDto(InstanceEndpoints.Id(job.Key), job.Type, InstanceEndpoints.Id(job.InstanceKey), job.ElementId, job.Retries, job.Deadline, job.Variables, job.CustomHeaders);
                    await context.Response.WriteAsync($"id: {job.Key.ToString(CultureInfo.InvariantCulture)}\nevent: job\ndata: {JsonSerializer.Serialize(dto, JsonSerializerOptions.Web)}\n\n", cancellationToken).ConfigureAwait(false);
                }

                if (jobs.Count == 0)
                {
                    await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken).ConfigureAwait(false);
                }

                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The worker disconnected.
        }
    }
}
