using System.Security.Claims;
using System.Text.Json.Nodes;
using Flowd.Api.Errors;
using Flowd.Api.Security;
using Flowd.Application.Decisions;
using Flowd.Application.Deployment;
using Flowd.Engine.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Request body of a decision evaluation.</summary>
public sealed record EvaluateDecisionRequest(JsonObject? Variables);

/// <summary>DMN decisions: deploy <c>.dmn</c> files and evaluate a decision (FR-DMN-003).</summary>
public static class DecisionEndpoints
{
    public static IEndpointRouteBuilder MapDecisionEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup("/v1/decisions");
        group.MapPost("/", DeployAsync).DisableAntiforgery().RequireAuthorization(Policies.Deploy);
        group.MapGet("/", ListAsync).RequireAuthorization(Policies.Read);
        group.MapPost("/{decisionId}/evaluate", EvaluateAsync).RequireAuthorization(Policies.Start);
        group.MapGet("/{decisionId}/xml", XmlAsync).RequireAuthorization(Policies.Read);
        return routes;
    }

    private static async Task<IResult> DeployAsync(ClaimsPrincipal user, HttpRequest request, DeployDecisionsUseCase useCase, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return FailureMapper.ToProblem(new Failure(new ValidationFailure("Send the DMN files as multipart/form-data.")));
        }

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var resources = new List<DeploymentResource>();
        foreach (var file in form.Files)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            resources.Add(new DeploymentResource(file.FileName, buffer.ToArray()));
        }

        return await useCase.ExecuteAsync(resources, user.TenantId(), cancellationToken).ConfigureAwait(false) switch
        {
            IReadOnlyList<DeployedDecision> deployed => Results.Ok(new { decisions = deployed.Select(d => new { key = d.Decision.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), decisionId = d.Decision.DecisionId, version = d.Decision.Version, isNewVersion = d.IsNewVersion }) }),
            Failure failure => FailureMapper.ToProblem(failure),
        };
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, IDecisionStore store, CancellationToken cancellationToken)
    {
        var items = await store.ListAsync(user.TenantId(), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { items = items.Select(d => new { key = d.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), decisionId = d.DecisionId, version = d.Version, resourceName = d.ResourceName }) });
    }

    /// <summary>The DMN file the latest version of the decision came from (the file may hold more decisions).</summary>
    private static async Task<IResult> XmlAsync(ClaimsPrincipal user, string decisionId, IDecisionStore store, CancellationToken cancellationToken)
    {
        var info = await store.FindAsync(user.TenantId(), decisionId, cancellationToken).ConfigureAwait(false);
        var bytes = info is null ? null : await store.GetResourceAsync(user.TenantId(), info.Key, cancellationToken).ConfigureAwait(false);
        return bytes is null ? FailureMapper.ToProblem(new Failure(new NotFound($"Decision '{decisionId}' not found."))) : Results.Bytes(bytes, "application/xml");
    }

    private static async Task<IResult> EvaluateAsync(ClaimsPrincipal user, string decisionId, EvaluateDecisionRequest? body, EvaluateDecisionUseCase useCase, CancellationToken cancellationToken) =>
        await useCase.ExecuteAsync(user.TenantId(), decisionId, body?.Variables, cancellationToken).ConfigureAwait(false) switch
        {
            DecisionEvaluation evaluation => Results.Ok(new { result = evaluation.Value }),
            Failure failure => FailureMapper.ToProblem(failure),
        };
}
