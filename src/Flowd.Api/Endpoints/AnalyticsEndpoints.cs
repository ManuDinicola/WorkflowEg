using System.Security.Claims;
using Flowd.Api.Security;
using Flowd.Application.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Counts, durations and the element heat map for the overview and analytics pages (FR-UI-004).</summary>
public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup("/v1/analytics");
        group.MapGet("/summary", SummaryAsync).RequireAuthorization(Policies.Read);
        group.MapGet("/processes/{bpmnProcessId}", ProcessAsync).RequireAuthorization(Policies.Read);
        return routes;
    }

    private static async Task<IResult> SummaryAsync(ClaimsPrincipal user, IAnalyticsQueries queries, CancellationToken cancellationToken)
    {
        var summary = await queries.SummaryAsync(user.TenantId(), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { instancesByState = summary.InstancesByState, openIncidents = summary.OpenIncidents, jobsByState = summary.JobsByState, openUserTasks = summary.OpenUserTasks });
    }

    private static async Task<IResult> ProcessAsync(ClaimsPrincipal user, string bpmnProcessId, IAnalyticsQueries queries, CancellationToken cancellationToken)
    {
        var stats = await queries.ProcessAsync(user.TenantId(), bpmnProcessId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            bpmnProcessId = stats.BpmnProcessId,
            instancesByState = stats.InstancesByState,
            averageDurationSeconds = stats.AverageDurationSeconds,
            maxDurationSeconds = stats.MaxDurationSeconds,
            elements = stats.Elements.Select(e => new { elementId = e.ElementId, elementType = e.ElementType, activations = e.Activations, averageSeconds = e.AverageSeconds }),
        });
    }
}
