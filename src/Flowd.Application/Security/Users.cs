using System.Security.Cryptography;

namespace Flowd.Application.Security;

/// <summary>A built-in user as listed (never carries the password or its hash).</summary>
public sealed record UserInfo(string Username, IReadOnlyList<string> Roles, string TenantId, IReadOnlyList<string> Groups, DateTimeOffset CreatedAt, bool Disabled);

/// <summary>What a login needs: the stored hash and the lockout state.</summary>
public sealed record UserCredential(UserInfo User, string PasswordHash, int FailedAttempts, DateTimeOffset? LockedUntil);

/// <summary>Port: built-in users (FR-SEC-011).</summary>
public interface IUserStore
{
    Task<UserCredential?> FindAsync(string username, CancellationToken cancellationToken);

    /// <summary>Counts a failed attempt (locking the user when <paramref name="lockUntil"/> is set) or, on success, clears the counter.</summary>
    Task RecordLoginAsync(string username, bool success, DateTimeOffset? lockUntil, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserInfo>> ListAsync(string? tenantId, CancellationToken cancellationToken);

    /// <summary>False when the username is taken.</summary>
    Task<bool> CreateAsync(string username, string passwordHash, UserInfo info, CancellationToken cancellationToken);

    /// <summary>Changes roles, groups, disabled flag and, when given, the password hash. With a tenant, only a user of that tenant. False when nothing matched.</summary>
    Task<bool> UpdateAsync(string username, string? tenantId, IReadOnlyList<string> roles, IReadOnlyList<string> groups, bool disabled, string? passwordHash, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string username, string? tenantId, CancellationToken cancellationToken);
}

/// <summary>Password hashing with PBKDF2-SHA256, 600 000 iterations (the OWASP recommendation), a random salt and a constant-time comparison.</summary>
public static class PasswordHasher
{
    public const int MinimumLength = 12;
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>A hash of a random password, verified for unknown usernames so that a missing user costs the same time as a wrong password.</summary>
    public static string Decoy { get; } = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));

    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        ArgumentNullException.ThrowIfNull(password);
        var parts = (stored ?? string.Empty).Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var iterations) || iterations is < 1 or > 5_000_000)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Login with lockout: 5 wrong passwords lock the user for 15 minutes. Unknown users, wrong passwords, disabled and locked users all look the same to the caller.</summary>
public sealed class LoginService(IUserStore store, TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    /// <summary>The user when the password is right and the account is usable; otherwise null.</summary>
    public async Task<UserInfo?> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var credential = string.IsNullOrWhiteSpace(username) ? null : await store.FindAsync(username.Trim(), cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var correct = PasswordHasher.Verify(password ?? string.Empty, credential?.PasswordHash ?? PasswordHasher.Decoy); // always one hash, so timing does not tell who exists
        if (credential is null || credential.User.Disabled || credential.LockedUntil is { } until && until > now)
        {
            return null;
        }

        if (correct)
        {
            await store.RecordLoginAsync(credential.User.Username, success: true, null, cancellationToken).ConfigureAwait(false);
            return credential.User;
        }

        DateTimeOffset? lockUntil = credential.FailedAttempts + 1 >= MaxFailures ? now + LockDuration : null;
        await store.RecordLoginAsync(credential.User.Username, success: false, lockUntil, cancellationToken).ConfigureAwait(false);
        return null;
    }
}
