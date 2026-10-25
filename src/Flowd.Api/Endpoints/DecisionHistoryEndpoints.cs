using System.Globalization;
using System.Security.Claims;
using Flowd.Api.Errors;
using Flowd.Api.Security;
using Flowd.Application.Decisions;
using Flowd.Engine.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>The history of decision evaluations: which decision ran with what input and gave what output (FR-DMN-007).</summary>
public static class DecisionHistoryEndpoints
{
    public static IEndpointRouteBuilder MapDecisionHistoryEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/decision-instances", ListAsync).RequireAuthorization(Policies.Read);
        routes.MapGet("/v1/decision-instances/{key}", GetAsync).RequireAuthorization(Policies.Read);
        return routes;
    }

    private static object ToDto(DecisionEvaluationRecord r) => new
    {
        key = r.Key.ToString(CultureInfo.InvariantCulture),
        decisionId = r.DecisionId,
        decisionDefinitionKey = r.DecisionKey?.ToString(CultureInfo.InvariantCulture),
        evaluatedAt = r.EvaluatedAt,
        source = r.Source,
        processInstanceKey = r.InstanceKey?.ToString(CultureInfo.InvariantCulture),
        durationMs = r.DurationMs,
        input = r.Input,
        output = r.Output,
        error = r.Error,
    };

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, IDecisionHistory history, string? decisionId, string? processInstanceKey, int? pageSize, string? after, CancellationToken cancellationToken)
    {
        var page = await history.ListAsync(
            user.TenantId(),
            decisionId,
            InstanceEndpoints.TryKey(processInstanceKey),
            Math.Clamp(pageSize ?? 50, 1, 500),
            InstanceEndpoints.TryKey(after),
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { items = page.Items.Select(ToDto), nextCursor = page.NextCursor?.ToString(CultureInfo.InvariantCulture) });
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, string key, IDecisionHistory history, CancellationToken cancellationToken)
    {
        var record = InstanceEndpoints.TryKey(key) is { } k ? await history.GetAsync(user.TenantId(), k, cancellationToken).ConfigureAwait(false) : null;
        return record is null ? FailureMapper.ToProblem(new Failure(new NotFound($"Decision instance '{key}' not found."))) : Results.Ok(ToDto(record));
    }
}
