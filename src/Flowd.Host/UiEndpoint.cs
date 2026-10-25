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
        routes.MapGet("/ui", () => Results.Bytes(Page.Value, "text/html; charset=utf-8")).AllowAnonymous();
        routes.MapGet("/", () => Results.Redirect("/ui")).AllowAnonymous();
        routes.MapGet("/ui/vendor/{file}", Vendor).AllowAnonymous();
        return routes;
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
