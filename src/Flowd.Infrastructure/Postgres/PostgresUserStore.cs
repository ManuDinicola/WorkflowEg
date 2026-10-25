using Dapper;
using Flowd.Application.Security;
using Npgsql;

namespace Flowd.Infrastructure.Postgres;

/// <summary>Built-in users in the <c>app_user</c> table (FR-SEC-011).</summary>
public sealed class PostgresUserStore(NpgsqlDataSource dataSource) : IUserStore
{
    private const string Columns = "username AS Username, password_hash AS PasswordHash, roles AS Roles, tenant_id AS TenantId, groups AS Groups, created_at AS CreatedAt, disabled_at AS DisabledAt, failed_attempts AS FailedAttempts, locked_until AS LockedUntil";

    public async Task<UserCredential?> FindAsync(string username, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM app_user WHERE username = @username", new { username }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        DateTimeOffset? lockedUntil = row.LockedUntil is { } until ? Utc(until) : null;
        return new UserCredential(ToInfo(row), row.PasswordHash, row.FailedAttempts, lockedUntil);
    }

    public async Task RecordLoginAsync(string username, bool success, DateTimeOffset? lockUntil, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = success
            ? "UPDATE app_user SET failed_attempts = 0, locked_until = NULL WHERE username = @username"
            : "UPDATE app_user SET failed_attempts = CASE WHEN @lockUntil::timestamptz IS NULL THEN failed_attempts + 1 ELSE 0 END, locked_until = @lockUntil WHERE username = @username";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { username, lockUntil = lockUntil?.UtcDateTime }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserInfo>> ListAsync(string? tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM app_user WHERE (@tenant::text IS NULL OR tenant_id = @tenant) ORDER BY username", new { tenant = tenantId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(ToInfo)];
    }

    public async Task<bool> CreateAsync(string username, string passwordHash, UserInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app_user (username, password_hash, roles, tenant_id, groups, created_at) VALUES (@username, @passwordHash, @roles, @tenant, @groups, @created) ON CONFLICT (username) DO NOTHING",
            new { username, passwordHash, roles = string.Join(',', info.Roles), tenant = info.TenantId, groups = string.Join(',', info.Groups), created = info.CreatedAt.UtcDateTime },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return inserted > 0;
    }

    public async Task<bool> UpdateAsync(string username, string? tenantId, IReadOnlyList<string> roles, IReadOnlyList<string> groups, bool disabled, string? passwordHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app_user SET roles = @roles, groups = @groups,
              disabled_at = CASE WHEN @disabled THEN COALESCE(disabled_at, now()) ELSE NULL END,
              password_hash = COALESCE(@passwordHash, password_hash),
              failed_attempts = CASE WHEN @passwordHash IS NULL THEN failed_attempts ELSE 0 END,
              locked_until = CASE WHEN @passwordHash IS NULL THEN locked_until ELSE NULL END
            WHERE username = @username AND (@tenant::text IS NULL OR tenant_id = @tenant)
            """,
            new { username, tenant = tenantId, roles = string.Join(',', roles), groups = string.Join(',', groups), disabled, passwordHash },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> DeleteAsync(string username, string? tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM app_user WHERE username = @username AND (@tenant::text IS NULL OR tenant_id = @tenant)",
            new { username, tenant = tenantId },
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    private static UserInfo ToInfo(Row r) => new(r.Username, Split(r.Roles), r.TenantId, Split(r.Groups), Utc(r.CreatedAt), r.DisabledAt is not null);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string[] Split(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record Row(string Username, string PasswordHash, string Roles, string TenantId, string Groups, DateTime CreatedAt, DateTime? DisabledAt, int FailedAttempts, DateTime? LockedUntil);
}
