using Flowd.Application.Readiness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Liveness and readiness endpoints (API-013).</summary>
public static class OperationalEndpoints
{
    public static IEndpointRouteBuilder MapOperationalEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        routes.MapGet("/readyz", ReadyAsync);
        return routes;
    }

    private static async Task<IResult> ReadyAsync(IReadinessProbe probe, CancellationToken cancellationToken)
    {
        var status = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);
        return status.IsReady
            ? Results.Ok(new { status = "ready" })
            : Results.Problem(
                title: "Not ready",
                detail: status.Detail,
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "UNAVAILABLE" });
    }
}
