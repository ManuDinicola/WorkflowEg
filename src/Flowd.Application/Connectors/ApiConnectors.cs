using System.Text.Json;
using System.Text.Json.Nodes;
using Flowd.Application.Jobs;
using Flowd.Bpmn.Model;

namespace Flowd.Application.Connectors;

/// <summary>
/// The GraphQL connector (FR-CON-015): <c>&lt;flowd:connector type="graphql" url="https://api.example.com/graphql" query="query($id: ID!) { order(id: $id) { total } }"
/// variables='{"id":"{{orderId}}"}' resultVariable="order"/&gt;</c>. It posts the query through the HTTP connector (same host allow-list, authentication settings and secrets).
/// The result variable gets <c>{ status, data, errors }</c>; a GraphQL <c>errors</c> list fails the job unless <c>failOnError="false"</c>.
/// </summary>
public sealed class GraphQlConnectorHandler(HttpConnectorHandler http, ISecretProvider secrets) : IConnectorHandler
{
    public string TaskType => ServiceTask.GraphQlConnectorTaskType;

    public async Task<ConnectorResult> ExecuteAsync(ActivatedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        JsonObject body;
        try
        {
            body = BuildBody(job);
        }
        catch (ConnectorException ex)
        {
            return ConnectorResult.Failed(ex.Message);
        }

        var request = HttpBased.Post(job, body);
        var result = await http.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        var resultVariable = job.CustomHeaders.GetValueOrDefault("resultVariable") ?? "response";
        var reply = result.Variables![resultVariable]!.AsObject();
        var answer = reply["body"] as JsonObject;
        var errors = answer?["errors"];
        var failOnError = !string.Equals(job.CustomHeaders.GetValueOrDefault("failOnError"), "false", StringComparison.OrdinalIgnoreCase);
        if (failOnError && errors is JsonArray { Count: > 0 } list)
        {
            return ConnectorResult.Failed("GraphQL error: " + (list[0]?["message"]?.GetValue<string>() ?? "unknown"));
        }

        return ConnectorResult.Ok(new JsonObject { [resultVariable] = new JsonObject { ["status"] = reply["status"]?.DeepClone(), ["data"] = answer?["data"]?.DeepClone(), ["errors"] = errors?.DeepClone() } });
    }

    private JsonObject BuildBody(ActivatedJob job)
    {
        var settings = job.CustomHeaders;
        var body = new JsonObject { ["query"] = settings.GetValueOrDefault("query") ?? throw new ConnectorException("The GraphQL connector needs a query.") };
        if (settings.TryGetValue("variables", out var variables))
        {
            try
            {
                body["variables"] = JsonNode.Parse(Template.Render(variables, job.Variables, secrets));
            }
            catch (JsonException)
            {
                throw new ConnectorException("The GraphQL variables must be a JSON object.");
            }
        }

        if (settings.TryGetValue("operationName", out var operation))
        {
            body["operationName"] = operation;
        }

        return body;
    }
}

/// <summary>
/// The Slack connector (FR-CON-016): <c>&lt;flowd:connector type="slack" token="{{secrets.SLACK_TOKEN}}" channel="#ops" text="Order {{id}} failed"/&gt;</c> posts a message with
/// <c>chat.postMessage</c> through the HTTP connector, so <c>slack.com</c> must be in the host allow-list. A Slack answer with <c>ok: false</c> fails the job.
/// The result variable gets <c>{ ts, channel }</c>.
/// </summary>
public sealed class SlackConnectorHandler(HttpConnectorHandler http, ISecretProvider secrets) : IConnectorHandler
{
    private const string PostMessage = "https://slack.com/api/chat.postMessage";

    public string TaskType => ServiceTask.SlackConnectorTaskType;

    public async Task<ConnectorResult> ExecuteAsync(ActivatedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var settings = job.CustomHeaders;
        if (!settings.TryGetValue("channel", out var channel) || !settings.TryGetValue("token", out var token))
        {
            return ConnectorResult.Failed("The Slack connector needs a channel and a token.");
        }

        JsonObject body;
        try
        {
            body = new JsonObject { ["channel"] = Template.Render(channel, job.Variables, secrets), ["text"] = Template.Render(settings.GetValueOrDefault("text") ?? string.Empty, job.Variables, secrets) };
        }
        catch (ConnectorException ex)
        {
            return ConnectorResult.Failed(ex.Message);
        }

        var request = HttpBased.Post(job, body);
        request = request with { CustomHeaders = new Dictionary<string, string>(request.CustomHeaders, StringComparer.Ordinal) { ["url"] = PostMessage, ["authType"] = "bearer", ["token"] = token, ["failOnError"] = "true" } };
        var result = await http.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        var resultVariable = settings.GetValueOrDefault("resultVariable") ?? "slack";
        var answer = result.Variables![settings.GetValueOrDefault("resultVariable") ?? "response"]?["body"] as JsonObject;
        if (answer?["ok"]?.GetValue<bool>() != true)
        {
            return ConnectorResult.Failed("Slack refused the message: " + (answer?["error"]?.GetValue<string>() ?? "unknown error"));
        }

        return ConnectorResult.Ok(new JsonObject { [resultVariable] = new JsonObject { ["ts"] = answer["ts"]?.DeepClone(), ["channel"] = answer["channel"]?.DeepClone() } });
    }
}

/// <summary>Shared by the connectors that are HTTP calls with a ready-made JSON body.</summary>
internal static class HttpBased
{
    /// <summary>The same job as an HTTP connector job: POST, the given JSON as the body, sent as it is (no second template pass, which could expose secrets through variable values).</summary>
    public static ActivatedJob Post(ActivatedJob job, JsonObject body)
    {
        var settings = new Dictionary<string, string>(job.CustomHeaders, StringComparer.Ordinal)
        {
            ["method"] = "POST",
            [HttpConnectorHandler.RawBodySetting] = body.ToJsonString(),
        };
        return job with { CustomHeaders = settings };
    }
}
