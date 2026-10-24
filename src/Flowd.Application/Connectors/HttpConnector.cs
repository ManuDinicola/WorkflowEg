using System.Text.Json;
using System.Text.Json.Nodes;
using Flowd.Application.Jobs;

namespace Flowd.Application.Connectors;

/// <summary>
/// The built-in HTTP connector (FR-CON-010). A service task with <c>&lt;flowd:connector type="http" url="..." method="POST" body="..."
/// headers="{...}" resultVariable="response" failOnError="true"/&gt;</c> calls the URL; all settings are templates (see <see cref="Template"/>).
/// The result variable gets <c>{ status, body }</c>, the body parsed as JSON when it is JSON. A non-2xx answer fails the job (retries apply)
/// unless <c>failOnError="false"</c>. Only hosts in <see cref="ConnectorOptions.AllowedHosts"/> can be called.
/// </summary>
public sealed class HttpConnectorHandler(IHttpCallSender sender, ISecretProvider secrets, ConnectorOptions options)
{
    public async Task<ConnectorResult> ExecuteAsync(ActivatedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            var call = Build(job);
            var reply = await sender.SendAsync(call, TimeSpan.FromSeconds(Math.Max(options.TimeoutSeconds, 1)), options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            var failOnError = !string.Equals(job.CustomHeaders.GetValueOrDefault("failOnError"), "false", StringComparison.OrdinalIgnoreCase);
            if (failOnError && reply.Status is < 200 or >= 300)
            {
                return ConnectorResult.Failed($"HTTP {reply.Status} from {call.Url.Host}.");
            }

            var resultVariable = job.CustomHeaders.GetValueOrDefault("resultVariable") ?? "response";
            return ConnectorResult.Ok(new JsonObject { [resultVariable] = new JsonObject { ["status"] = reply.Status, ["body"] = ParseBody(reply) } });
        }
        catch (ConnectorException ex)
        {
            return ConnectorResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return ConnectorResult.Failed($"The HTTP call failed: {ex.GetType().Name}.");
        }
    }

    private HttpCall Build(ActivatedJob job)
    {
        var settings = job.CustomHeaders;
        var urlText = Render(settings.GetValueOrDefault("url") ?? throw new ConnectorException("The connector needs a url."), job);
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            throw new ConnectorException("The connector url must be an absolute http or https address.");
        }

        if (!HostAllowed(url.Host))
        {
            throw new ConnectorException($"Host '{url.Host}' is not in Flowd:Connectors:AllowedHosts.");
        }

        var method = (settings.GetValueOrDefault("method") ?? "GET").ToUpperInvariant();
        var body = settings.TryGetValue("body", out var bodyTemplate) ? Render(bodyTemplate, job) : null;
        return new HttpCall(method, url, Headers(settings.GetValueOrDefault("headers"), job), body);
    }

    private bool HostAllowed(string host) =>
        options.AllowedHosts.Any(h => h == "*" || string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    private string Render(string template, ActivatedJob job) => Template.Render(template, job.Variables, secrets);

    private Dictionary<string, string> Headers(string? json, ActivatedJob job)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            foreach (var (name, value) in (JsonNode.Parse(Render(json, job)) as JsonObject) ?? [])
            {
                result[name] = value?.ToString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            throw new ConnectorException("The connector headers must be a JSON object such as {\"Accept\":\"application/json\"}.");
        }

        return result;
    }

    private static JsonNode? ParseBody(HttpReply reply)
    {
        if (reply.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                return JsonNode.Parse(reply.Body);
            }
            catch (JsonException)
            {
                // not valid JSON after all: keep the text
            }
        }

        return reply.Body;
    }
}
