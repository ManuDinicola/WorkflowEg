using System.Security.Claims;
using Flowd.Api.Errors;
using Flowd.Api.Security;
using Flowd.Application.Security;
using Flowd.Contracts.Security;
using Flowd.Engine.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Administration of API keys: list, create (the key is shown once) and revoke (FR-SEC-008). Admin role only.</summary>
public static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup("/v1/api-keys");
        group.MapGet("/", ListAsync).RequireAuthorization(Policies.Admin);
        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.Admin);
        group.MapDelete("/{id}", RevokeAsync).RequireAuthorization(Policies.Admin);
        return routes;
    }

    /// <summary>A caller bound to a tenant sees and changes only that tenant's keys; a caller without a tenant sees all.</summary>
    private static string? Scope(ClaimsPrincipal user) => user.TenantId() is { Length: > 0 } tenant ? tenant : null;

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, IApiKeyStore store, CancellationToken cancellationToken)
    {
        var keys = await store.ListAsync(Scope(user), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { items = keys.Select(k => new ApiKeyDto(k.Id, k.Info.Name, k.Info.Roles, k.Info.TenantId, k.Info.Processes ?? [], k.CreatedAt, k.RevokedAt)) });
    }

    private static async Task<IResult> CreateAsync(ClaimsPrincipal user, CreateApiKeyRequestDto request, IApiKeyStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        var unknown = request.Roles?.Where(r => !Roles.All.Contains(r, StringComparer.Ordinal)).ToList() ?? [];
        if (string.IsNullOrWhiteSpace(request.Name) || request.Roles is not { Count: > 0 } || unknown.Count > 0)
        {
            return FailureMapper.ToProblem(new Failure(new ValidationFailure($"A name and at least one role of {string.Join(", ", Roles.All)} are required.")));
        }

        var tenant = Scope(user) ?? request.Tenant ?? string.Empty;
        var processes = request.Processes ?? [];
        var key = ApiKeyHasher.NewKey();
        var hash = ApiKeyHasher.Hash(key);
        await store.CreateAsync(hash, new ApiKeyInfo(request.Name.Trim(), request.Roles, tenant, processes), time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new CreatedApiKeyDto(Convert.ToHexStringLower(hash)[..12], key, request.Name.Trim(), request.Roles, tenant, processes));
    }

    private static async Task<IResult> RevokeAsync(ClaimsPrincipal user, string id, IApiKeyStore store, TimeProvider time, CancellationToken cancellationToken) =>
        await store.RevokeAsync(id, Scope(user), time.GetUtcNow(), cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : FailureMapper.ToProblem(new Failure(new NotFound($"API key '{id}' not found or already revoked.")));
}
