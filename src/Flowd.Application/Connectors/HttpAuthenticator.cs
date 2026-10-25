using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowd.Application.Jobs;

namespace Flowd.Application.Connectors;

/// <summary>
/// Adds authentication to a connector's HTTP call (FR-CON-014), chosen by the task setting <c>authType</c>:
/// <c>bearer</c> (<c>token</c>), <c>basic</c> (<c>username</c>, <c>password</c>) or <c>oauth2</c> (client credentials: <c>tokenUrl</c>, <c>clientId</c>,
/// <c>clientSecret</c>, optional <c>scope</c> and <c>clientAuthentication="basicAuthHeader"</c>). All values are templates, so secrets belong in
/// <c>{{secrets.NAME}}</c>. OAuth tokens are kept in memory until shortly before they expire and are never written to variables or history.
/// </summary>
public sealed class HttpAuthenticator(IHttpCallSender sender, ISecretProvider secrets, ConnectorOptions options, TimeProvider time)
{
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);

    public async Task<HttpCall> ApplyAsync(HttpCall call, ActivatedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(job);
        var settings = job.CustomHeaders;
        var authorization = settings.GetValueOrDefault("authType")?.ToLowerInvariant() switch
        {
            null or "" or "none" => null,
            "bearer" => "Bearer " + Required(settings, "token", job),
            "basic" => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Required(settings, "username", job)}:{Required(settings, "password", job)}")),
            "oauth2" => "Bearer " + await TokenAsync(job, cancellationToken).ConfigureAwait(false),
            _ => throw new ConnectorException("authType must be none, bearer, basic or oauth2."), // non-union: setting text
        };
        if (authorization is null)
        {
            return call;
        }

        var headers = new Dictionary<string, string>(call.Headers, StringComparer.OrdinalIgnoreCase) { ["Authorization"] = authorization };
        return call with { Headers = headers };
    }

    private async Task<string> TokenAsync(ActivatedJob job, CancellationToken cancellationToken)
    {
        var settings = job.CustomHeaders;
        var tokenUrl = Required(settings, "tokenUrl", job);
        var clientId = Required(settings, "clientId", job);
        var clientSecret = Required(settings, "clientSecret", job);
        var scope = settings.TryGetValue("scope", out var scopeTemplate) ? Template.Render(scopeTemplate, job.Variables, secrets) : string.Empty;
        if (!Uri.TryCreate(tokenUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || !options.AllowsHost(url.Host))
        {
            throw new ConnectorException("The tokenUrl must be an http or https address on a host in Flowd:Connectors:AllowedHosts.");
        }

        var cacheKey = $"{tokenUrl}|{clientId}|{scope}";
        var now = time.GetUtcNow();
        if (_tokens.TryGetValue(cacheKey, out var cached) && cached.Expires - Margin > now)
        {
            return cached.Token;
        }

        var useHeader = string.Equals(settings.GetValueOrDefault("clientAuthentication"), "basicAuthHeader", StringComparison.OrdinalIgnoreCase);
        var form = new List<string> { "grant_type=client_credentials" };
        if (scope.Length > 0)
        {
            form.Add("scope=" + Uri.EscapeDataString(scope));
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/x-www-form-urlencoded", ["Accept"] = "application/json" };
        if (useHeader)
        {
            headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        }
        else
        {
            form.Add("client_id=" + Uri.EscapeDataString(clientId));
            form.Add("client_secret=" + Uri.EscapeDataString(clientSecret));
        }

        var reply = await sender.SendAsync(new HttpCall("POST", url, headers, string.Join('&', form)), TimeSpan.FromSeconds(Math.Max(options.TimeoutSeconds, 1)), options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        if (reply.Status is < 200 or >= 300)
        {
            throw new ConnectorException($"The token request to {url.Host} failed (HTTP {reply.Status}).");
        }

        var (token, lifetime) = ParseToken(reply.Body);
        _tokens[cacheKey] = (token, now + lifetime);
        return token;
    }

    private static (string Token, TimeSpan Lifetime) ParseToken(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject json && json["access_token"]?.GetValue<string>() is { Length: > 0 } token)
            {
                var seconds = json["expires_in"] is JsonValue v && v.TryGetValue<double>(out var s) ? s : 300;
                return (token, TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 86_400)));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // fall through to the error below
        }

        throw new ConnectorException("The token answer did not contain an access_token.");
    }

    private string Required(IReadOnlyDictionary<string, string> settings, string name, ActivatedJob job) =>
        Template.Render(settings.GetValueOrDefault(name) ?? throw new ConnectorException($"authType needs the setting '{name}'."), job.Variables, secrets);
}
