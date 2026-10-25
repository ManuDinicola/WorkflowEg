using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Flowd.Application.Security;
using Flowd.Infrastructure.Postgres;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>FR-SEC-011: built-in users log in with a password and get an access token that works like any other.</summary>
[Trait("Category", "Integration")]
public sealed class UserLoginTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Password = "correct horse battery";

    private static readonly string SigningKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("a-test-signing-key-of-at-least-32-bytes!!"));

    private static Task<TestApp> StartAsync(PostgresFixture postgres) =>
        TestApp.StartAsync(postgres, secure: true, $"--Flowd:Auth:JwtSigningKey={SigningKey}", "--Flowd:Auth:JwtIssuer=flowd-test");

    private static async Task SeedAsync(string connectionString, string username, string roles, string tenant = "")
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var info = new UserInfo(username, roles.Split(','), tenant, [], DateTimeOffset.UtcNow, Disabled: false);
        (await new PostgresUserStore(dataSource).CreateAsync(username, PasswordHasher.Hash(Password), info, CancellationToken.None)).ShouldBeTrue();
    }

    private static async Task<HttpResponseMessage> LoginAsync(TestApp app, string username, string password) =>
        await app.Client.PostAsJsonAsync(new Uri("/v1/login", UriKind.Relative), new { username, password });

    private static async Task<HttpClient> SignedInAsync(TestApp app, string username, string password = Password)
    {
        var reply = (await (await LoginAsync(app, username, password)).Content.ReadFromJsonAsync<JsonObject>())!;
        var client = new HttpClient { BaseAddress = app.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reply["accessToken"]!.GetValue<string>());
        return client;
    }

    [Fact]
    public async Task FR_SEC_011_a_user_logs_in_and_the_token_carries_its_roles()
    {
        await using var app = await StartAsync(postgres);
        await SeedAsync(app.ConnectionString, "alice", "admin");
        await SeedAsync(app.ConnectionString, "rita", "reader");
        using var admin = await SignedInAsync(app, "alice");
        using var reader = await SignedInAsync(app, "rita");

        (await admin.GetAsync(new Uri("/v1/users", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reader.GetAsync(new Uri("/v1/users", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reader.GetAsync(new Uri("/v1/process-instances", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.Client.GetAsync(new Uri("/v1/process-instances", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FR_SEC_011_wrong_passwords_unknown_users_and_disabled_users_all_get_the_same_answer()
    {
        await using var app = await StartAsync(postgres);
        await SeedAsync(app.ConnectionString, "alice", "admin");
        await SeedAsync(app.ConnectionString, "dora", "reader");
        using var admin = await SignedInAsync(app, "alice");
        (await admin.PutAsJsonAsync(new Uri("/v1/users/dora", UriKind.Relative), new { roles = new[] { "reader" }, disabled = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var wrong = await LoginAsync(app, "alice", "not the password");
        var unknown = await LoginAsync(app, "nobody", Password);
        var disabled = await LoginAsync(app, "dora", Password);

        new[] { wrong.StatusCode, unknown.StatusCode, disabled.StatusCode }.ShouldAllBe(s => s == HttpStatusCode.Unauthorized);
        (await wrong.Content.ReadAsStringAsync()).ShouldBe(await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FR_SEC_011_five_wrong_passwords_lock_the_user_even_for_the_right_password_until_an_admin_sets_a_new_one()
    {
        await using var app = await StartAsync(postgres);
        await SeedAsync(app.ConnectionString, "alice", "admin");
        await SeedAsync(app.ConnectionString, "bob", "reader");
        using var admin = await SignedInAsync(app, "alice");
        for (var i = 0; i < LoginService.MaxFailures; i++)
        {
            (await LoginAsync(app, "bob", "wrong password " + i)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await LoginAsync(app, "bob", Password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await admin.PutAsJsonAsync(new Uri("/v1/users/bob", UriKind.Relative), new { roles = new[] { "reader" }, password = "a brand new password" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await LoginAsync(app, "bob", "a brand new password")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(app, "bob", Password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FR_SEC_011_an_admin_creates_lists_and_deletes_users_and_short_passwords_are_refused()
    {
        await using var app = await StartAsync(postgres);
        await SeedAsync(app.ConnectionString, "alice", "admin");
        using var admin = await SignedInAsync(app, "alice");

        (await admin.PostAsJsonAsync(new Uri("/v1/users", UriKind.Relative), new { username = "carl", password = "short", roles = new[] { "reader" } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(new Uri("/v1/users", UriKind.Relative), new { username = "carl", password = Password, roles = new[] { "wizard" } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(new Uri("/v1/users", UriKind.Relative), new { username = "carl", password = Password, roles = new[] { "operator" }, groups = new[] { "ops" } })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync(new Uri("/v1/users", UriKind.Relative), new { username = "carl", password = Password, roles = new[] { "operator" } })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var listed = await admin.GetStringAsync(new Uri("/v1/users", UriKind.Relative));
        listed.ShouldContain("carl");
        listed.ShouldNotContain("pbkdf2");
        listed.ShouldNotContain(Password);
        (await LoginAsync(app, "carl", Password)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.DeleteAsync(new Uri("/v1/users/carl", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LoginAsync(app, "carl", Password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FR_SEC_011_a_tenant_admin_only_manages_users_of_its_own_tenant_and_login_is_off_without_a_signing_key()
    {
        await using var app = await StartAsync(postgres);
        await SeedAsync(app.ConnectionString, "tina", "admin", "acme");
        await SeedAsync(app.ConnectionString, "otto", "reader", "beta");
        using var tenantAdmin = await SignedInAsync(app, "tina");

        var created = (await (await tenantAdmin.PostAsJsonAsync(new Uri("/v1/users", UriKind.Relative), new { username = "tim", password = Password, roles = new[] { "reader" }, tenant = "beta" })).Content.ReadFromJsonAsync<JsonObject>())!;

        created["tenant"]!.GetValue<string>().ShouldBe("acme");
        (await tenantAdmin.DeleteAsync(new Uri("/v1/users/otto", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonNode.Parse(await tenantAdmin.GetStringAsync(new Uri("/v1/users", UriKind.Relative)))!["items"]!.AsArray().Select(i => i!["tenant"]!.GetValue<string>()).Distinct().ShouldBe(["acme"]);
        await using var plain = await TestApp.StartAsync(postgres, secure: true);
        (await plain.Client.PostAsJsonAsync(new Uri("/v1/login", UriKind.Relative), new { username = "x", password = Password })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
