using System.Security.Claims;
using Flowd.Api.Errors;
using Flowd.Api.Security;
using Flowd.Application.Security;
using Flowd.Contracts.Security;
using Flowd.Engine.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Flowd.Api.Endpoints;

/// <summary>
/// Built-in users (FR-SEC-011): <c>POST /v1/login</c> (public) exchanges a username and password for an access token; administrators manage users with
/// <c>/v1/users</c>. Login needs <c>Flowd:Auth:JwtSigningKey</c> and <c>JwtIssuer</c>; without them it answers 404, as if it did not exist.
/// </summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/login", LoginAsync).AllowAnonymous();
        var group = routes.MapGroup("/v1/users");
        group.MapGet("/", ListAsync).RequireAuthorization(Policies.Admin);
        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.Admin);
        group.MapPut("/{username}", UpdateAsync).RequireAuthorization(Policies.Admin);
        group.MapDelete("/{username}", DeleteAsync).RequireAuthorization(Policies.Admin);
        return routes;
    }

    private static string? Scope(ClaimsPrincipal user) => user.TenantId() is { Length: > 0 } tenant ? tenant : null;

    private static UserDto ToDto(UserInfo u) => new(u.Username, u.Roles, u.TenantId, u.Groups, u.CreatedAt, u.Disabled);

    private static async Task<IResult> LoginAsync(LoginRequestDto request, LoginService login, IOptions<AuthOptions> options, TimeProvider time, CancellationToken cancellationToken)
    {
        var auth = options.Value;
        if (string.IsNullOrWhiteSpace(auth.JwtSigningKey) || string.IsNullOrWhiteSpace(auth.JwtIssuer))
        {
            return Results.NotFound();
        }

        var user = await login.LoginAsync(request.Username, request.Password, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Problem(title: "Login failed", detail: "Invalid username or password.", statusCode: StatusCodes.Status401Unauthorized, extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "LOGIN_FAILED" });
        }

        var lifetime = TimeSpan.FromMinutes(Math.Clamp(auth.LoginTokenMinutes, 1, 24 * 60));
        var now = time.GetUtcNow();
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = user.Username,
            [auth.NameClaim] = user.Username,
            [auth.RoleClaim] = user.Roles,
            [auth.TenantClaim] = user.TenantId,
            [auth.GroupsClaim] = user.Groups,
        };
        var token = Hs256Token.Create(claims, Convert.FromBase64String(auth.JwtSigningKey), now, now + lifetime, auth.JwtIssuer, auth.JwtAudience);
        return Results.Ok(new LoginResponseDto(token, (int)lifetime.TotalSeconds));
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, IUserStore store, CancellationToken cancellationToken) =>
        Results.Ok(new { items = (await store.ListAsync(Scope(user), cancellationToken).ConfigureAwait(false)).Select(ToDto) });

    private static async Task<IResult> CreateAsync(ClaimsPrincipal caller, CreateUserRequestDto request, IUserStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        if (Validate(request.Username, request.Password, request.Roles) is { } problem)
        {
            return problem;
        }

        var tenant = Scope(caller) ?? request.Tenant ?? string.Empty;
        var info = new UserInfo(request.Username.Trim(), request.Roles, tenant, request.Groups ?? [], time.GetUtcNow(), Disabled: false);
        var created = await store.CreateAsync(info.Username, PasswordHasher.Hash(request.Password), info, cancellationToken).ConfigureAwait(false);
        return created
            ? Results.Ok(ToDto(info))
            : FailureMapper.ToProblem(new Failure(new Conflict("USER_EXISTS", $"User '{info.Username}' already exists.")));
    }

    private static async Task<IResult> UpdateAsync(ClaimsPrincipal caller, string username, UpdateUserRequestDto request, IUserStore store, CancellationToken cancellationToken)
    {
        if (Validate(username, request.Password ?? new string('x', PasswordHasher.MinimumLength), request.Roles) is { } problem)
        {
            return problem;
        }

        var hash = request.Password is null ? null : PasswordHasher.Hash(request.Password);
        return await store.UpdateAsync(username, Scope(caller), request.Roles, request.Groups ?? [], request.Disabled ?? false, hash, cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : FailureMapper.ToProblem(new Failure(new NotFound($"User '{username}' not found.")));
    }

    private static async Task<IResult> DeleteAsync(ClaimsPrincipal caller, string username, IUserStore store, CancellationToken cancellationToken) =>
        await store.DeleteAsync(username, Scope(caller), cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : FailureMapper.ToProblem(new Failure(new NotFound($"User '{username}' not found.")));

    private static IResult? Validate(string username, string password, IReadOnlyList<string>? roles)
    {
        var unknown = roles?.Where(r => !Roles.All.Contains(r, StringComparer.Ordinal)).ToList() ?? [];
        if (string.IsNullOrWhiteSpace(username) || username.Length > 100 || roles is not { Count: > 0 } || unknown.Count > 0)
        {
            return FailureMapper.ToProblem(new Failure(new ValidationFailure($"A username (at most 100 characters) and at least one role of {string.Join(", ", Roles.All)} are required.")));
        }

        return password is { Length: >= PasswordHasher.MinimumLength }
            ? null
            : FailureMapper.ToProblem(new Failure(new ValidationFailure($"The password needs at least {PasswordHasher.MinimumLength} characters.")));
    }
}
