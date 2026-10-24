using System.Security.Claims;
using Flowd.Api.Security;
using Flowd.Application.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Job search for operators: find stuck, failed or long-running jobs (FR-API-011).</summary>
public static class JobSearchEndpoints
{
    private const int MaxPageSize = 1000;

    public static IEndpointRouteBuilder MapJobSearchEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/jobs", SearchAsync).RequireAuthorization(Policies.Read);
        return routes;
    }

    private static async Task<IResult> SearchAsync(ClaimsPrincipal user, IJobQueries queries, string? type, string? state, string? processInstanceKey, string? worker, int? pageSize, string? after, CancellationToken cancellationToken)
    {
        var page = await queries.SearchAsync(
            user.TenantId(),
            new JobFilter(type, state, InstanceEndpoints.TryKey(processInstanceKey), worker),
            Math.Clamp(pageSize ?? 100, 1, MaxPageSize),
            InstanceEndpoints.TryKey(after),
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            items = page.Items.Select(j => new
            {
                key = InstanceEndpoints.Id(j.Key),
                type = j.Type,
                processInstanceKey = InstanceEndpoints.Id(j.InstanceKey),
                elementId = j.ElementId,
                state = j.State,
                retries = j.Retries,
                worker = j.Worker,
                deadline = j.Deadline,
                errorMessage = j.ErrorMessage,
                createdAt = j.CreatedAt,
            }),
            nextCursor = page.NextCursor is { } c ? InstanceEndpoints.Id(c) : null,
        });
    }
}
