using System.Reflection;
using Flowd.Api.Security;
using Flowd.Application.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Which engine nodes are running on this database, and the version of the node that answers (FR-OPS-011).</summary>
public static class TopologyEndpoint
{
    public static IEndpointRouteBuilder MapTopologyEndpoint(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/topology", GetAsync).RequireAuthorization(Policies.Read);
        return routes;
    }

    private static async Task<IResult> GetAsync(INodeDirectory directory, CancellationToken cancellationToken)
    {
        var nodes = await directory.RunningNodeIdsAsync(cancellationToken).ConfigureAwait(false);
        var version = typeof(TopologyEndpoint).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return Results.Ok(new { nodes = nodes.Select(id => new { nodeId = id }), nodeCount = nodes.Count, version });
    }
}
