using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowd.Application.Connectors;
using Flowd.Application.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>
/// Inbound webhook connector (FR-CON-011): other systems post JSON to <c>/v1/connectors/webhook/{name}</c>; the body becomes the variables
/// of a message called <c>{name}</c> whose correlation key is read from the body. The call is authenticated by an HMAC-SHA256 signature of
/// the raw body in the <c>X-Flowd-Signature</c> header (<c>sha256=&lt;hex&gt;</c>, the same scheme as the outgoing webhooks), not by an API key.
/// </summary>
public static class WebhookEndpoints
{
    public const string SignatureHeader = "X-Flowd-Signature";

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/connectors/webhook/{name}", ReceiveAsync).AllowAnonymous();
        return routes;
    }

    private static async Task<IResult> ReceiveAsync(HttpRequest request, string name, ConnectorOptions options, MessageService messages, CancellationToken cancellationToken)
    {
        if (!options.Webhooks.TryGetValue(name, out var settings) || string.IsNullOrEmpty(settings.Secret))
        {
            return Results.NotFound(); // no such webhook; says nothing about which names exist
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var body = buffer.ToArray();
        if (!SignatureValid(settings.Secret, body, request.Headers[SignatureHeader].ToString()))
        {
            return Results.Json(new { code = "UNAUTHENTICATED", detail = "Missing or wrong signature." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            payload = null;
        }

        var key = payload is null ? null : Lookup(payload, settings.CorrelationKey);
        if (payload is null || key is null)
        {
            return Results.BadRequest(new { code = "VALIDATION_ERROR", detail = $"The body must be a JSON object with a '{settings.CorrelationKey}' value." });
        }

        var result = await messages.PublishAsync(new PublishMessageRequest(settings.Tenant, name, key, payload, TimeSpan.FromMinutes(1), null), cancellationToken).ConfigureAwait(false);
        return result switch
        {
            PublishedMessage published => Results.Accepted(value: new { correlations = published.Correlations }),
            Flowd.Engine.Results.Failure failure => Errors.FailureMapper.ToProblem(failure),
        };
    }

    private static bool SignatureValid(string secret, byte[] body, string header)
    {
        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(header[prefix.Length..]));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? Lookup(JsonObject payload, string path)
    {
        JsonNode? node = payload;
        foreach (var part in path.Split('.'))
        {
            node = (node as JsonObject)?[part];
        }

        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value => value.ToJsonString().Trim('"'),
            _ => null, // non-union: JSON kind
        };
    }
}
