using System.Reflection;

namespace Flowd.Host;

/// <summary>
/// Serves the built-in web UI: one embedded page plus the embedded bpmn-js modeler files, so it works from a single-file publish and without
/// internet access (FR-UI-001, FR-UI-005). It is public; every API call it makes is authenticated.
/// </summary>
internal static class UiEndpoint
{
    private const string VendorPrefix = "Flowd.Ui.vendor.";

    private static readonly Lazy<byte[]> Page = new(() => Read("Flowd.Ui.index.html") ?? throw new InvalidOperationException("The UI page is not embedded."));

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
    };

    public static IEndpointRouteBuilder MapUi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/ui", (HttpContext context, IConfiguration configuration) =>
        {
            Harden(context, configuration);
            return Results.Bytes(Page.Value, "text/html; charset=utf-8");
        }).AllowAnonymous();
        routes.MapGet("/", () => Results.Redirect("/ui")).AllowAnonymous();
        routes.MapGet("/ui/vendor/{file}", Vendor).AllowAnonymous();
        routes.MapGet("/ui/config", Config).AllowAnonymous();
        return routes;
    }

    /// <summary>
    /// Response headers of the page (FR-SEC-010): no framing, no content sniffing, no referrer, and a content security policy that only allows
    /// the page's own scripts and styles and calls to itself and to the configured identity provider. The token the page keeps in the browser
    /// is what this protects, so the policy is strict about where code can come from.
    /// </summary>
    internal static void Harden(HttpContext context, IConfiguration configuration)
    {
        var authority = configuration["Flowd:Ui:Oidc:Authority"] ?? configuration["Flowd:Auth:JwtAuthority"];
        var connect = string.IsNullOrWhiteSpace(authority) || !Uri.TryCreate(authority, UriKind.Absolute, out var uri) ? "'self'" : $"'self' {uri.GetLeftPart(UriPartial.Authority)}";
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = $"default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src {connect}; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
    }

    /// <summary>
    /// What the page needs to offer "Sign in" (FR-SEC-007): the OpenID Connect authority and the public client id of the UI. Nothing secret.
    /// Set <c>Flowd:Ui:Oidc:ClientId</c> (and optionally <c>Authority</c>, which defaults to <c>Flowd:Auth:JwtAuthority</c>, and <c>Scopes</c>).
    /// </summary>
    private static IResult Config(IConfiguration configuration)
    {
        var section = configuration.GetSection("Flowd:Ui:Oidc");
        var localLogin = !string.IsNullOrWhiteSpace(configuration["Flowd:Auth:JwtSigningKey"]) && !string.IsNullOrWhiteSpace(configuration["Flowd:Auth:JwtIssuer"]);
        var clientId = section["ClientId"];
        var authority = section["Authority"] ?? configuration["Flowd:Auth:JwtAuthority"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(authority))
        {
            return Results.Ok(new { oidc = (object?)null, localLogin });
        }

        return Results.Ok(new { oidc = new { authority = authority.TrimEnd('/'), clientId, scopes = section["Scopes"] ?? "openid profile" }, localLogin });
    }

    private static IResult Vendor(string file)
    {
        if (!ContentTypes.TryGetValue(Path.GetExtension(file), out var contentType) || Read(VendorPrefix + file) is not { } bytes)
        {
            return Results.NotFound();
        }

        return Results.Bytes(bytes, contentType);
    }

    private static byte[]? Read(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
