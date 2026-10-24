using System.Net;
using Flowd.Host;
using Flowd.Infrastructure.Postgres;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>FR-DB-001 (migrations) and API-013 (health endpoints) against a real PostgreSQL.</summary>
[Trait("Category", "Integration")]
public sealed class MigrationAndHealthTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly string[] ExpectedTables =
    [
        "process_definition", "process_instance", "element_instance", "variable", "job", "timer",
        "message_subscription", "buffered_message", "incident", "user_task", "history_event", "outbox", "idempotency_key",
    ];

    [Fact]
    public async Task FR_DB_001_migrate_creates_schema_and_is_idempotent()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var runner = new PostgresMigrationRunner(cs);

        runner.IsCurrent().ShouldBeFalse();
        runner.Migrate().Count.ShouldBe(1);
        runner.IsCurrent().ShouldBeTrue();
        runner.Migrate().ShouldBeEmpty();

        var tables = await ListTablesAsync(cs);
        foreach (var table in ExpectedTables)
        {
            tables.ShouldContain(table);
        }
    }

    [Fact]
    public async Task API_013_healthz_is_200_even_when_database_is_not_migrated()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var app = await StartAsync(cs);

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        (await client.GetAsync(new Uri("/healthz", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task API_013_readyz_is_503_until_migrated_then_200()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var app = await StartAsync(cs);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        (await client.GetAsync(new Uri("/readyz", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        new PostgresMigrationRunner(cs).Migrate();

        (await client.GetAsync(new Uri("/readyz", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<WebApplication> StartAsync(string connectionString)
    {
        var app = FlowdHostBuilder.Build(["--urls", "http://127.0.0.1:0", $"--Flowd:ConnectionString={connectionString}"]);
        await app.StartAsync();
        return app;
    }

    private static async Task<List<string>> ListTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
