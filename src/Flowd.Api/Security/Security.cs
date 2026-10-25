using System.Security.Claims;
using System.Text.Encodings.Web;
using Flowd.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Flowd.Api.Security;

/// <summary>Authorization policy names, one per kind of caller (FR-SEC-002).</summary>
public static class Policies
{
    public const string Read = "flowd.read";
    public const string Deploy = "flowd.deploy";
    public const string Start = "flowd.start";
    public const string Operate = "flowd.operate";
    public const string Work = "flowd.work";
    public const string Publish = "flowd.publish";
    public const string UserTask = "flowd.usertask";
    public const string Audit = "flowd.audit";
}

/// <summary>Settings of the authentication stack, bound from <c>Flowd:Auth</c>.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Flowd:Auth";

    /// <summary>Authentication is on by default; only the explicit dev flag turns it off (FR-SEC-001).</summary>
    public bool Insecure { get; init; }

    public string? JwtAuthority { get; init; }

    public string? JwtAudience { get; init; }

    /// <summary>Expected issuer when validating with a signing key.</summary>
    public string? JwtIssuer { get; init; }

    /// <summary>Base64 HS256 signing key, an alternative to an OIDC authority for simple deployments.</summary>
    public string? JwtSigningKey { get; init; }

    public string RoleClaim { get; init; } = "role";

    public string TenantClaim { get; init; } = "tenant";
}

public static class ClaimsExtensions
{
    public const string TenantClaimType = "flowd:tenant";

    /// <summary>Process ids the caller may deploy, start and batch-operate on; no such claim means all (FR-SEC-006).</summary>
    public const string ProcessClaimType = "flowd:processes";

    /// <summary>True when the caller may use the process id: no restriction claim, or the id is listed.</summary>
    public static bool MayUseProcess(this ClaimsPrincipal user, string? bpmnProcessId)
    {
        ArgumentNullException.ThrowIfNull(user);
        var allowed = user.FindAll(ProcessClaimType).Select(c => c.Value).ToList();
        return allowed.Count == 0 || (bpmnProcessId is not null && allowed.Contains(bpmnProcessId, StringComparer.Ordinal));
    }

    /// <summary>The process ids the caller is limited to, or null when unrestricted.</summary>
    public static IReadOnlySet<string>? AllowedProcesses(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var allowed = user.FindAll(ProcessClaimType).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        return allowed.Count == 0 ? null : allowed;
    }

    /// <summary>The caller's tenant; the empty string in single-tenant or insecure mode (FR-SEC-003).</summary>
    public static string TenantId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.FindFirst(TenantClaimType)?.Value ?? string.Empty;
    }
}

/// <summary>Authenticates <c>X-Api-Key</c> against the hashed keys in the database (FR-SEC-001).</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyStore keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var presented) || string.IsNullOrWhiteSpace(presented))
        {
            return AuthenticateResult.NoResult();
        }

        var info = await keys.FindAsync(ApiKeyHasher.Hash(presented.ToString()), Context.RequestAborted).ConfigureAwait(false);
        if (info is null)
        {
            return AuthenticateResult.Fail("Unknown API key.");
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, info.Name), new(ClaimsExtensions.TenantClaimType, info.TenantId) };
        claims.AddRange(info.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange((info.Processes ?? []).Select(p => new Claim(ClaimsExtensions.ProcessClaimType, p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}

/// <summary>Maps JWT claims to the engine's role and tenant claim types.</summary>
public sealed class FlowdClaimsTransformation(IOptions<AuthOptions> options) : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity || identity.HasClaim(c => c.Type == ClaimsExtensions.TenantClaimType))
        {
            return Task.FromResult(principal);
        }

        var transformed = new ClaimsIdentity(identity);
        transformed.AddClaim(new Claim(ClaimsExtensions.TenantClaimType, principal.FindFirst(options.Value.TenantClaim)?.Value ?? string.Empty));
        foreach (var role in principal.FindAll(options.Value.RoleClaim).Where(c => !transformed.HasClaim(ClaimTypes.Role, c.Value)))
        {
            transformed.AddClaim(new Claim(ClaimTypes.Role, role.Value));
        }

        return Task.FromResult(new ClaimsPrincipal(transformed));
    }
}

public static class SecurityRegistration
{
    private static readonly Dictionary<string, string[]> RolesByPolicy = new(StringComparer.Ordinal)
    {
        [Policies.Read] = [Roles.Admin, Roles.Operator, Roles.Developer, Roles.Reader],
        [Policies.Deploy] = [Roles.Admin, Roles.Developer],
        [Policies.Start] = [Roles.Admin, Roles.Operator, Roles.Developer],
        [Policies.Operate] = [Roles.Admin, Roles.Operator],
        [Policies.Work] = [Roles.Admin, Roles.Worker],
        [Policies.Publish] = [Roles.Admin, Roles.Operator, Roles.Worker],
        [Policies.UserTask] = [Roles.Admin, Roles.Operator, Roles.Worker],
        [Policies.Audit] = [Roles.Admin],
    };

    public static void AddAuthorizationPolicies(this IServiceCollection services, bool insecure)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAuthorization(options =>
        {
            foreach (var (policy, roles) in RolesByPolicy)
            {
                options.AddPolicy(policy, builder =>
                {
                    if (insecure)
                    {
                        builder.RequireAssertion(_ => true);
                    }
                    else
                    {
                        builder.RequireAuthenticatedUser().RequireRole(roles);
                    }
                });
            }
        });
    }

    /// <summary>Writes RFC 9457 bodies for 401 and 403 with the stable codes of API-R4.</summary>
    public static IApplicationBuilder UseProblemStatusCodes(this IApplicationBuilder app) =>
        app.UseStatusCodePages(async context =>
        {
            var status = context.HttpContext.Response.StatusCode;
            var (code, title) = status switch
            {
                StatusCodes.Status401Unauthorized => ("UNAUTHENTICATED", "Authentication required"),
                StatusCodes.Status403Forbidden => ("PERMISSION_DENIED", "Permission denied"),
                StatusCodes.Status429TooManyRequests => ("RESOURCE_EXHAUSTED", "Too many requests"),
                _ => (null, null), // non-union: HTTP status
            };
            if (code is null)
            {
                return;
            }

            context.HttpContext.Response.ContentType = "application/problem+json";
            await context.HttpContext.Response.WriteAsJsonAsync(new { title, status, code }, context.HttpContext.RequestAborted).ConfigureAwait(false);
        });
}
