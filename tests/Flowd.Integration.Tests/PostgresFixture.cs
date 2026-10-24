using Npgsql;
using Testcontainers.PostgreSql;

namespace Flowd.Integration.Tests;

/// <summary>
/// One Postgres server per test run. Uses <c>FLOWD_TEST_POSTGRES</c> (a connection string to an existing server)
/// when set, otherwise starts <c>postgres:$POSTGRES_IMAGE_TAG</c> (default 16) with Testcontainers (needs Docker).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _adminConnectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var existing = Environment.GetEnvironmentVariable("FLOWD_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(existing))
        {
            _adminConnectionString = existing;
            return;
        }

        var tag = Environment.GetEnvironmentVariable("POSTGRES_IMAGE_TAG") ?? "16";
        _container = new PostgreSqlBuilder($"postgres:{tag}").Build();
        await _container.StartAsync();
        _adminConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates an empty, uniquely named database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"flowd_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }
}
