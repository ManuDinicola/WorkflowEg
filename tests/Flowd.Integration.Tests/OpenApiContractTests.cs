using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>API contract (section 14): the published OpenAPI document must match the committed route snapshot.</summary>
[Trait("Category", "Integration")]
public sealed class OpenApiContractTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task OpenApi_document_routes_match_the_committed_snapshot()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var document = await app.Client.GetFromJsonAsync<JsonObject>(new Uri("/openapi/v1.json", UriKind.Relative));
        var routes = document!["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(operation => $"{operation.Key.ToUpperInvariant()} {path.Key}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var snapshot = Path.Combine(RepositoryRoot(), "docs", "openapi-routes.txt");
        if (Environment.GetEnvironmentVariable("FLOWD_UPDATE_SNAPSHOT") == "1")
        {
            await File.WriteAllLinesAsync(snapshot, routes);
        }

        routes.ShouldBe(await File.ReadAllLinesAsync(snapshot));
    }

    [Theory]
    [InlineData("POST /v1/deployments")]
    [InlineData("POST /v1/process-instances")]
    [InlineData("POST /v1/jobs/activate")]
    [InlineData("POST /v1/jobs/{key}/complete")]
    [InlineData("POST /v1/messages")]
    public async Task Core_routes_are_documented(string route)
    {
        await using var app = await TestApp.StartAsync(postgres);
        var document = await app.Client.GetFromJsonAsync<JsonObject>(new Uri("/openapi/v1.json", UriKind.Relative));
        var parts = route.Split(' ');
        document!["paths"]![parts[1]]!.AsObject().ContainsKey(parts[0].ToLowerInvariant()).ShouldBeTrue();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Flowd.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
