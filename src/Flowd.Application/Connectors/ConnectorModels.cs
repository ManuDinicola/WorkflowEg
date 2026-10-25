using System.Text.Json.Nodes;

namespace Flowd.Application.Connectors;

/// <summary>Settings of the built-in connectors, bound from <c>Flowd:Connectors</c> (FR-CON-010).</summary>
public sealed class ConnectorOptions
{
    public const string SectionName = "Flowd:Connectors";

    /// <summary>
    /// Hosts the HTTP connector may call (exact names, case-insensitive). Empty means the connector refuses every call;
    /// <c>*</c> allows any host (only for networks you trust: redirects are never followed, but internal addresses are reachable).
    /// </summary>
    public string[] AllowedHosts { get; init; } = [];

    /// <summary>True when <paramref name="host"/> is in <see cref="AllowedHosts"/> (or the list has <c>*</c>).</summary>
    public bool AllowsHost(string host) => AllowedHosts.Any(h => h == "*" || string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    public int TimeoutSeconds { get; init; } = 10;

    public int MaxResponseBytes { get; init; } = 1_048_576;

    /// <summary>Inbound webhooks by message name (FR-CON-011).</summary>
    public Dictionary<string, WebhookSettings> Webhooks { get; init; } = new(StringComparer.Ordinal);

    /// <summary>SMTP server of the e-mail connector (FR-CON-012).</summary>
    public SmtpSettings Smtp { get; init; } = new();

    /// <summary>Message broker of the queue connector (FR-CON-013).</summary>
    public QueueSettings Queue { get; init; } = new();

    /// <summary>Databases the SQL connector may query (FR-CON-017).</summary>
    public SqlSettings Sql { get; init; } = new();

    /// <summary>Kafka brokers of the Kafka connector (FR-CON-018).</summary>
    public KafkaSettings Kafka { get; init; } = new();
}

/// <summary>Port: named secrets for connector templates (<c>{{secrets.NAME}}</c>). Secrets never appear in process variables or history.</summary>
public interface ISecretProvider
{
    string? Find(string name);
}

/// <summary>An HTTP request a connector wants to send.</summary>
public sealed record HttpCall(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>The answer to an <see cref="HttpCall"/>; <see cref="Body"/> is cut at the configured maximum.</summary>
public sealed record HttpReply(int Status, string? ContentType, string Body);

/// <summary>Port: sends HTTP requests for the connector (never follows redirects).</summary>
public interface IHttpCallSender
{
    Task<HttpReply> SendAsync(HttpCall request, TimeSpan timeout, int maxResponseBytes, CancellationToken cancellationToken);
}

/// <summary>What the connector wants done with its job.</summary>
public sealed record ConnectorResult(bool Success, JsonObject? Variables, string? Message)
{
    public static ConnectorResult Ok(JsonObject variables) => new(true, variables, null);

    public static ConnectorResult Failed(string message) => new(false, null, message);
}

/// <summary>A template referred to something that is not there, or the connector settings are invalid.</summary>
public sealed class ConnectorException(string message) : Exception(message);

/// <summary>One inbound webhook: <c>Flowd:Connectors:Webhooks:{name}</c>. The request body must carry an HMAC-SHA256 signature made with <see cref="Secret"/>.</summary>
public sealed class WebhookSettings
{
    /// <summary>Shared secret; a webhook without a secret does not exist.</summary>
    public string Secret { get; init; } = string.Empty;

    /// <summary>Tenant that receives the message (empty in single-tenant mode).</summary>
    public string Tenant { get; init; } = string.Empty;

    /// <summary>Property of the JSON body (dotted path) that holds the correlation key. Default <c>id</c>.</summary>
    public string CorrelationKey { get; init; } = "id";
}
