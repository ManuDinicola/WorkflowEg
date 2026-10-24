using System.Globalization;
using Flowd.Api.Errors;
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
    private const string DefaultTenant = "";
    private const int MaxPageSize = 1000;

    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1");
        group.MapPost("/deployments", DeployAsync).DisableAntiforgery();
        group.MapGet("/process-definitions", ListAsync);
        group.MapGet("/process-definitions/{key}", GetAsync);
        group.MapGet("/process-definitions/{key}/xml", GetXmlAsync);
        return routes;
    }

    private static async Task<IResult> DeployAsync(
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

        var result = await useCase.ExecuteAsync(resources, DefaultTenant, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            IReadOnlyList<DeployedDefinition> deployed => Results.Ok(ToResponse(deployed)),
            Failure failure => FailureMapper.ToProblem(failure),
        };
    }

    private static async Task<IResult> ListAsync(
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

        var page = await store.ListAsync(DefaultTenant, size, afterKey, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new PageDto<ProcessDefinitionDto>(
            [.. page.Items.Select(ToDto)],
            page.NextCursor?.ToString(CultureInfo.InvariantCulture)));
    }

    private static async Task<IResult> GetAsync(string key, IProcessDefinitionStore store, CancellationToken cancellationToken)
    {
        if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return FailureMapper.ToProblem(new Failure(new NotFound($"Process definition '{key}' not found.")));
        }

        var info = await store.GetAsync(DefaultTenant, parsed, cancellationToken).ConfigureAwait(false);
        return info is null
            ? FailureMapper.ToProblem(new Failure(new NotFound($"Process definition '{key}' not found.")))
            : Results.Ok(ToDto(info));
    }

    private static async Task<IResult> GetXmlAsync(string key, IProcessDefinitionStore store, CancellationToken cancellationToken)
    {
        if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return FailureMapper.ToProblem(new Failure(new NotFound($"Process definition '{key}' not found.")));
        }

        var xml = await store.GetResourceAsync(DefaultTenant, parsed, cancellationToken).ConfigureAwait(false);
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
        info.DeployedAt);
}
