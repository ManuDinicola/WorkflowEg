using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Flowd.Api.Security;

/// <summary>
/// Creates the HS256 access tokens that built-in users get at login (FR-SEC-011). They are checked by the same JWT validation as tokens from an
/// identity provider, with the key and issuer of <c>Flowd:Auth</c>, so a login token is accepted exactly where such a token would be.
/// </summary>
public static class Hs256Token
{
    public static string Create(IReadOnlyDictionary<string, object> claims, byte[] key, DateTimeOffset notBefore, DateTimeOffset expires, string issuer, string? audience)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(key);
        var payload = new Dictionary<string, object>(claims, StringComparer.Ordinal)
        {
            ["iss"] = issuer,
            ["nbf"] = notBefore.ToUnixTimeSeconds(),
            ["iat"] = notBefore.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
        };
        if (audience is not null)
        {
            payload["aud"] = audience;
        }

        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>(StringComparer.Ordinal) { ["alg"] = "HS256", ["typ"] = "JWT" }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = Base64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{header}.{body}")));
        return $"{header}.{body}.{signature}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
