using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flowd.Application.Security;
using Flowd.Infrastructure.Postgres.Execution;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>FR-SEC-008: administrators list, create and revoke API keys.</summary>
[Trait("Category", "Integration")]
public sealed class ApiKeyAdminTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static async Task<string> SeedAsync(string connectionString, string roles, string tenant)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var key = ApiKeyHasher.NewKey();
        await new PostgresApiKeyStore(dataSource).CreateAsync(ApiKeyHasher.Hash(key), new ApiKeyInfo("seed-" + roles + tenant, roles.Split(','), tenant), DateTimeOffset.UtcNow, CancellationToken.None);
        return key;
    }

    private static HttpClient As(TestApp app, string key)
    {
        var client = new HttpClient { BaseAddress = app.Client.BaseAddress };
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    [Fact]
    public async Task FR_SEC_008_an_admin_creates_a_key_that_works_and_revokes_it()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);
        using var admin = As(app, await SeedAsync(app.ConnectionString, "admin", string.Empty));

        var created = await admin.PostAsJsonAsync(new Uri("/v1/api-keys", UriKind.Relative), new { name = "ci", roles = new[] { "reader" }, tenant = "t1" });
        var body = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        using var reader = As(app, body["key"]!.GetValue<string>());

        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reader.GetAsync(new Uri("/v1/process-instances", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.DeleteAsync(new Uri("/v1/api-keys/" + body["id"]!.GetValue<string>(), UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await reader.GetAsync(new Uri("/v1/process-instances", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await admin.DeleteAsync(new Uri("/v1/api-keys/" + body["id"]!.GetValue<string>(), UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetStringAsync(new Uri("/v1/api-keys", UriKind.Relative))).ShouldNotContain(body["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task FR_SEC_008_only_admins_may_manage_keys_and_bad_requests_are_refused()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);
        using var developer = As(app, await SeedAsync(app.ConnectionString, "developer", string.Empty));
        using var admin = As(app, await SeedAsync(app.ConnectionString, "admin", string.Empty));

        (await developer.GetAsync(new Uri("/v1/api-keys", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync(new Uri("/v1/api-keys", UriKind.Relative), new { name = "x", roles = new[] { "superuser" } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(new Uri("/v1/api-keys", UriKind.Relative), new { name = " ", roles = new[] { "reader" } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FR_SEC_008_a_tenant_admin_only_sees_creates_and_revokes_keys_of_its_own_tenant()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);
        using var global = As(app, await SeedAsync(app.ConnectionString, "admin", string.Empty));
        using var tenantAdmin = As(app, await SeedAsync(app.ConnectionString, "admin", "acme"));
        var other = (await (await global.PostAsJsonAsync(new Uri("/v1/api-keys", UriKind.Relative), new { name = "other", roles = new[] { "reader" }, tenant = "beta" })).Content.ReadFromJsonAsync<JsonObject>())!;

        var mine = (await (await tenantAdmin.PostAsJsonAsync(new Uri("/v1/api-keys", UriKind.Relative), new { name = "mine", roles = new[] { "reader" }, tenant = "beta" })).Content.ReadFromJsonAsync<JsonObject>())!;
        var listed = JsonNode.Parse(await tenantAdmin.GetStringAsync(new Uri("/v1/api-keys", UriKind.Relative)))!["items"]!.AsArray();

        mine["tenant"]!.GetValue<string>().ShouldBe("acme");
        listed.Select(i => i!["tenant"]!.GetValue<string>()).Distinct().ShouldBe(["acme"]);
        (await tenantAdmin.DeleteAsync(new Uri("/v1/api-keys/" + other["id"]!.GetValue<string>(), UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
