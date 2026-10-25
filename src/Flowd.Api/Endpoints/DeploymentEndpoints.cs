using System.Globalization;
using System.Security.Claims;
using Flowd.Api.Errors;
using Flowd.Api.Security;
using Flowd.Application.Deployment;
using Flowd.Contracts.Deployments;
using Flowd.Engine.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Deployments and process definitions (API-001, API-002, FR-DEP-005).</summary>
public static class DeploymentEndpoints
{
    // Single-tenant mode until FR-SEC-003 lands in M7.
    private const int MaxPageSize = 1000;

    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1");
        group.MapPost("/deployments", DeployAsync).DisableAntiforgery().RequireAuthorization(Policies.Deploy);
        group.MapPost("/deployments/validate", ValidateAsync).DisableAntiforgery().RequireAuthorization(Policies.Deploy);
        group.MapGet("/process-definitions", ListAsync).RequireAuthorization(Policies.Read);
        group.MapGet("/process-definitions/{key}", GetAsync).RequireAuthorization(Policies.Read);
        group.MapGet("/process-definitions/{key}/xml", GetXmlAsync).RequireAuthorization(Policies.Read);
        group.MapDelete("/process-definitions/{key}", DeleteAsync).RequireAuthorization(Policies.Deploy);
        return routes;
    }

    /// <summary>Checks BPMN files like a deployment would, without deploying them (FR-DEP-008). Always answers 200 with the list of problems.</summary>
    private static async Task<IResult> ValidateAsync(HttpRequest request, DeployProcessesUseCase useCase, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return FailureMapper.ToProblem(new Failure(new ValidationFailure("Send the BPMN files as multipart/form-data.")));
        }

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var resources = new List<DeploymentResource>();
        foreach (var file in form.Files)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            resources.Add(new DeploymentResource(file.FileName, buffer.ToArray()));
        }

        var problems = useCase.Validate(resources);
        return Results.Ok(new { valid = problems.Count == 0 && resources.Count > 0, problems = problems.Select(p => new { target = p.Target, code = p.Code, message = p.Message }) });
    }

    private static async Task<IResult> DeployAsync(
        ClaimsPrincipal user,
        HttpRequest request,
        DeployProcessesUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return FailureMapper.ToProblem(new Failure(new ValidationFailure("Send the BPMN files as multipart/form-data.")));
        }

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var resources = new List<DeploymentResource>();
        foreach (var file in form.Files)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            resources.Add(new DeploymentResource(file.FileName, buffer.ToArray()));
        }

        var result = await useCase.ExecuteAsync(resources, user.TenantId(), user.AllowedProcesses(), cancellationToken).ConfigureAwait(false);
        return result switch
        {
            IReadOnlyList<DeployedDefinition> deployed => Results.Ok(ToResponse(deployed)),
            Failure failure => FailureMapper.ToProblem(failure),
        };
    }

    private static async Task<IResult> DeleteAsync(ClaimsPrincipal user, string key, IProcessDefinitionStore store, CancellationToken cancellationToken)
    {
        if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var definitionKey))
        {
            return FailureMapper.ToProblem(new Failure(new NotFound("Process definition not found.")));
        }

        return await store.DeleteAsync(user.TenantId(), definitionKey, cancellationToken).ConfigureAwait(false) switch
        {
            DeleteDefinitionOutcome.Deleted => Results.NoContent(),
            DeleteDefinitionOutcome.InUse => FailureMapper.ToProblem(new Failure(new Conflict("DEFINITION_IN_USE", "Instances of this version exist; delete or finish them first."))),
            _ => FailureMapper.ToProblem(new Failure(new NotFound("Process definition not found."))), // non-union: enum
        };
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        IProcessDefinitionStore store,
        int? pageSize,
        string? after,
        CancellationToken cancellationToken)
    {
        var size = Math.Clamp(pageSize ?? 100, 1, MaxPageSize);
        long? afterKey = null;
        if (after is not null)
        {
            if (!long.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return FailureMapper.ToProblem(new Failure(new ValidationFailure("Invalid 'after' cursor.")));
            }

            afterKey = parsed;
        }

        var page = await store.ListAsync(user.TenantId(), size, afterKey, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new PageDto<ProcessDefinitionDto>(
            [.. page.Items.Select(ToDto)],
            page.NextCursor?.ToString(CultureInfo.InvariantCulture)));
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, string key, IProcessDefinitionStore store, CancellationToken cancellationToken)
    {
        if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return FailureMapper.ToProblem(new Failure(new NotFound($"Process definition '{key}' not found.")));
        }

        var info = await store.GetAsync(user.TenantId(), parsed, cancellationToken).ConfigureAwait(false);
        return info is null
            ? FailureMapper.ToProblem(new Failure(new NotFound($"Process definition '{key}' not found.")))
            : Results.Ok(ToDto(info));
    }

    private static async Task<IResult> GetXmlAsync(ClaimsPrincipal user, string key, IProcessDefinitionStore store, CancellationToken cancellationToken)
    {
        if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return FailureMapper.ToProblem(new Failure(new NotFound($"Process definition '{key}' not found.")));
        }

        var xml = await store.GetResourceAsync(user.TenantId(), parsed, cancellationToken).ConfigureAwait(false);
        return xml is null
            ? FailureMapper.ToProblem(new Failure(new NotFound($"Process definition '{key}' not found.")))
            : Results.Bytes(xml, "application/xml");
    }

    private static DeploymentResponse ToResponse(IReadOnlyList<DeployedDefinition> deployed) =>
        new([.. deployed.Select(d => new DeployedProcessDto(ToDto(d.Definition), d.IsNewVersion))]);

    private static ProcessDefinitionDto ToDto(ProcessDefinitionInfo info) => new(
        info.Key.ToString(CultureInfo.InvariantCulture),
        info.BpmnProcessId,
        info.Version,
        info.ResourceName,
        info.DeployedAt,
        info.VersionTag);
}
