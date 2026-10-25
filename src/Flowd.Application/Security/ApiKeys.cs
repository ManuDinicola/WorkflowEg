using System.Security.Cryptography;
using System.Text;

namespace Flowd.Application.Security;

/// <summary>Roles of section 18 (FR-SEC-002).</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Developer = "developer";
    public const string Worker = "worker";
    public const string Reader = "reader";

    public static IReadOnlyList<string> All { get; } = [Admin, Operator, Developer, Worker, Reader];
}

/// <summary>An API key as stored: only its hash is kept (FR-SEC-001).</summary>
/// <summary>An API key: <c>Processes</c> limits deploy, start and batch operations to these process ids (null or empty: all).</summary>
public sealed record ApiKeyInfo(string Name, IReadOnlyList<string> Roles, string TenantId, IReadOnlyList<string>? Processes = null);

/// <summary>Port: static API keys for workers and automation (FR-SEC-001).</summary>
public interface IApiKeyStore
{
    Task<ApiKeyInfo?> FindAsync(byte[] keyHash, CancellationToken cancellationToken);

    Task CreateAsync(byte[] keyHash, ApiKeyInfo info, DateTimeOffset createdAt, CancellationToken cancellationToken);
}

/// <summary>Creates and hashes API keys. The plain key is shown once and never stored.</summary>
public static class ApiKeyHasher
{
    public const string Prefix = "flowd_";

    public static string NewKey() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
