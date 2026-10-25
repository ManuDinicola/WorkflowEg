using System.Reflection;

namespace Flowd.Host;

/// <summary>Serves the built-in web UI: one embedded page, so it works from a single-file publish (FR-UI-001). It is public; every API call it makes is authenticated.</summary>
internal static class UiEndpoint
{
    private static readonly Lazy<byte[]> Page = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Flowd.Ui.index.html")
            ?? throw new InvalidOperationException("The UI page is not embedded.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    public static IEndpointRouteBuilder MapUi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/ui", () => Results.Bytes(Page.Value, "text/html; charset=utf-8")).AllowAnonymous();
        routes.MapGet("/", () => Results.Redirect("/ui")).AllowAnonymous();
        return routes;
    }
}
