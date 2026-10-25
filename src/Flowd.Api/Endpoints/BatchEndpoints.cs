using System.Globalization;
using System.Security.Claims;
using Flowd.Api.Security;
using Flowd.Application.Batch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Request body of the batch cancel: restrict to one process id and/or cap the number of instances.</summary>
public sealed record BatchCancelRequest(string? BpmnProcessId, int? MaxItems);

/// <summary>Request body of the batch incident resolution.</summary>
public sealed record BatchResolveRequest(string? ProcessInstanceKey, int? MaxItems);

/// <summary>Request body of the batch migration: every active instance of the process moves to the target version.</summary>
public sealed record BatchMigrateRequest(string BpmnProcessId, string TargetDefinitionKey, IReadOnlyDictionary<string, string>? Mapping, int? MaxItems);

/// <summary>Batch operations for operators (FR-OPS-003).</summary>
public static class BatchEndpoints
{
    public static IEndpointRouteBuilder MapBatchEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup("/v1/batch");
        group.MapPost("/cancel-instances", CancelAsync).RequireAuthorization(Policies.Operate);
        group.MapPost("/resolve-incidents", ResolveAsync).RequireAuthorization(Policies.Operate);
        group.MapPost("/migrate-instances", MigrateAsync).RequireAuthorization(Policies.Operate);
        return routes;
    }

    private static async Task<IResult> CancelAsync(ClaimsPrincipal user, BatchCancelRequest? request, BatchService batch, CancellationToken cancellationToken) =>
        user.AllowedProcesses() is not null && !user.MayUseProcess(request?.BpmnProcessId)
            ? Results.Json(new { code = "PERMISSION_DENIED", detail = "This API key is limited to certain processes; name one of them in bpmnProcessId." }, statusCode: StatusCodes.Status403Forbidden)
            :
        Results.Ok(ToResponse(await batch.CancelInstancesAsync(user.TenantId(), request?.BpmnProcessId, request?.MaxItems, cancellationToken).ConfigureAwait(false)));

    private static async Task<IResult> ResolveAsync(ClaimsPrincipal user, BatchResolveRequest? request, BatchService batch, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await batch.ResolveIncidentsAsync(user.TenantId(), InstanceEndpoints.TryKey(request?.ProcessInstanceKey), request?.MaxItems, cancellationToken).ConfigureAwait(false)));


    private static async Task<IResult> MigrateAsync(ClaimsPrincipal user, BatchMigrateRequest request, BatchService batch, CancellationToken cancellationToken)
    {
        if (!user.MayUseProcess(request.BpmnProcessId))
        {
            return Results.Json(new { code = "PERMISSION_DENIED", detail = "This API key may not use that process." }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (InstanceEndpoints.TryKey(request.TargetDefinitionKey) is not { } target || string.IsNullOrWhiteSpace(request.BpmnProcessId))
        {
            return Results.BadRequest(new { code = "VALIDATION_ERROR", detail = "bpmnProcessId and a numeric targetDefinitionKey are required." });
        }

        return Results.Ok(ToResponse(await batch.MigrateInstancesAsync(user.TenantId(), request.BpmnProcessId, target, request.Mapping, request.MaxItems, cancellationToken).ConfigureAwait(false)));
    }

    private static object ToResponse(BatchResult result) => new
    {
        matched = result.Matched,
        succeeded = result.Succeeded,
        failed = result.Failed,
        failedKeys = result.FailedKeys.Select(k => k.ToString(CultureInfo.InvariantCulture)),
        truncated = result.Truncated,
    };
}
