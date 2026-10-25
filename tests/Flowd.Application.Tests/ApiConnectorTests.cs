using System.Text.Json.Nodes;
using Flowd.Application.Connectors;
using Flowd.Application.Jobs;
using Microsoft.Extensions.Time.Testing;

namespace Flowd.Application.Tests;

/// <summary>FR-CON-014..016: HTTP authentication (bearer, basic, OAuth 2.0), the GraphQL connector and the Slack connector.</summary>
public sealed class ApiConnectorTests
{
    private sealed class Secrets : ISecretProvider
    {
        public string? Find(string name) => name switch { "ID" => "client-1", "SECRET" => "shh", "SLACK" => "xoxb-1", _ => null };
    }

    private sealed class Scripted(Func<HttpCall, HttpReply> answer) : IHttpCallSender
    {
        public List<HttpCall> Calls { get; } = [];

        public Task<HttpReply> SendAsync(HttpCall request, TimeSpan timeout, int maxResponseBytes, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static ActivatedJob Job(Dictionary<string, string> settings, string variables = "{}") =>
        new(1, "t", 2, "A", 3, DateTimeOffset.UnixEpoch, JsonNode.Parse(variables)!.AsObject(), settings, DateTimeOffset.UnixEpoch);

    private static (HttpConnectorHandler Http, FakeTimeProvider Clock) Http(Scripted sender)
    {
        var clock = new FakeTimeProvider();
        var options = new ConnectorOptions { AllowedHosts = ["api.example.com", "auth.example.com", "slack.com"] };
        var secrets = new Secrets();
        return (new HttpConnectorHandler(sender, secrets, options, new HttpAuthenticator(sender, secrets, options, clock)), clock);
    }

    private static HttpReply Json(string body, int status = 200) => new(status, "application/json", body);

    [Fact]
    public async Task FR_CON_014_bearer_and_basic_authentication_set_the_authorization_header()
    {
        var sender = new Scripted(_ => Json("{}"));
        var (http, _) = Http(sender);

        await http.ExecuteAsync(Job(new() { ["url"] = "https://api.example.com/a", ["authType"] = "bearer", ["token"] = "{{secrets.SLACK}}" }), CancellationToken.None);
        await http.ExecuteAsync(Job(new() { ["url"] = "https://api.example.com/a", ["authType"] = "basic", ["username"] = "u", ["password"] = "{{secrets.SECRET}}" }), CancellationToken.None);

        sender.Calls[0].Headers["Authorization"].ShouldBe("Bearer xoxb-1");
        sender.Calls[1].Headers["Authorization"].ShouldBe("Basic " + Convert.ToBase64String("u:shh"u8.ToArray()));
    }

    [Fact]
    public async Task FR_CON_014_oauth2_fetches_a_token_once_uses_it_and_fetches_a_new_one_after_it_expires()
    {
        var sender = new Scripted(call => call.Url.Host == "auth.example.com" ? Json("""{"access_token":"tok-A","expires_in":120}""") : Json("{}"));
        var (http, clock) = Http(sender);
        var settings = new Dictionary<string, string> { ["url"] = "https://api.example.com/a", ["authType"] = "oauth2", ["tokenUrl"] = "https://auth.example.com/token", ["clientId"] = "{{secrets.ID}}", ["clientSecret"] = "{{secrets.SECRET}}", ["scope"] = "read write" };

        await http.ExecuteAsync(Job(settings), CancellationToken.None);
        await http.ExecuteAsync(Job(settings), CancellationToken.None);
        sender.Calls.Count(c => c.Url.Host == "auth.example.com").ShouldBe(1);
        clock.Advance(TimeSpan.FromSeconds(100)); // 120 s lifetime minus the 30 s margin
        await http.ExecuteAsync(Job(settings), CancellationToken.None);

        sender.Calls.Count(c => c.Url.Host == "auth.example.com").ShouldBe(2);
        var tokenRequest = sender.Calls.First(c => c.Url.Host == "auth.example.com");
        tokenRequest.Body.ShouldBe("grant_type=client_credentials&scope=read%20write&client_id=client-1&client_secret=shh");
        tokenRequest.Headers["Content-Type"].ShouldBe("application/x-www-form-urlencoded");
        sender.Calls.First(c => c.Url.Host == "api.example.com").Headers["Authorization"].ShouldBe("Bearer tok-A");
    }

    [Fact]
    public async Task FR_CON_014_oauth2_can_send_the_client_in_a_basic_header_and_refuses_bad_token_urls_and_answers()
    {
        var sender = new Scripted(call => call.Url.Host == "auth.example.com" ? Json("""{"access_token":"t","expires_in":60}""") : Json("{}"));
        var (http, _) = Http(sender);
        var header = new Dictionary<string, string> { ["url"] = "https://api.example.com/a", ["authType"] = "oauth2", ["tokenUrl"] = "https://auth.example.com/token", ["clientId"] = "c", ["clientSecret"] = "s", ["clientAuthentication"] = "basicAuthHeader" };

        await http.ExecuteAsync(Job(header), CancellationToken.None);
        var elsewhere = new Dictionary<string, string>(header) { ["tokenUrl"] = "https://evil.example.net/token" };
        var broken = new Scripted(call => call.Url.Host == "auth.example.com" ? Json("""{"nope":1}""") : Json("{}"));
        var (brokenHttp, _) = Http(broken);

        sender.Calls[0].Headers["Authorization"].ShouldBe("Basic " + Convert.ToBase64String("c:s"u8.ToArray()));
        sender.Calls[0].Body!.ShouldNotContain("client_secret");
        (await http.ExecuteAsync(Job(elsewhere), CancellationToken.None)).Message!.ShouldContain("tokenUrl");
        (await brokenHttp.ExecuteAsync(Job(header), CancellationToken.None)).Message!.ShouldContain("access_token");
        (await http.ExecuteAsync(Job(new(header) { ["authType"] = "kerberos" }), CancellationToken.None)).Message!.ShouldContain("authType");
    }

    [Fact]
    public async Task FR_CON_015_the_graphql_connector_posts_the_query_and_returns_data_or_fails_on_errors()
    {
        var sender = new Scripted(call => call.Body!.Contains("boom", StringComparison.Ordinal) ? Json("""{"errors":[{"message":"nope"}]}""") : Json("""{"data":{"order":{"total":5}}}"""));
        var (http, _) = Http(sender);
        var graph = new GraphQlConnectorHandler(http, new Secrets());
        var settings = new Dictionary<string, string> { ["url"] = "https://api.example.com/graphql", ["query"] = "query($id: ID!) { order(id: $id) { total } }", ["variables"] = """{"id":"{{id}}"}""", ["resultVariable"] = "order" };

        var ok = await graph.ExecuteAsync(Job(settings, """{"id":"o-1"}"""), CancellationToken.None);
        var failed = await graph.ExecuteAsync(Job(new(settings) { ["variables"] = """{"id":"boom"}""" }), CancellationToken.None);
        var lenient = await graph.ExecuteAsync(Job(new(settings) { ["variables"] = """{"id":"boom"}""", ["failOnError"] = "false" }), CancellationToken.None);

        ok.Variables!["order"]!["data"]!["order"]!["total"]!.GetValue<int>().ShouldBe(5);
        var sent = JsonNode.Parse(sender.Calls[0].Body!)!;
        sent["variables"]!["id"]!.GetValue<string>().ShouldBe("o-1");
        sender.Calls[0].Method.ShouldBe("POST");
        failed.Message.ShouldBe("GraphQL error: nope");
        lenient.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task FR_CON_015_a_variable_value_that_looks_like_a_placeholder_is_never_expanded_into_a_secret()
    {
        var sender = new Scripted(_ => Json("""{"data":{}}"""));
        var (http, _) = Http(sender);
        var graph = new GraphQlConnectorHandler(http, new Secrets());
        var settings = new Dictionary<string, string> { ["url"] = "https://api.example.com/graphql", ["query"] = "{ a }", ["variables"] = """{"note":"{{note}}"}""" };

        await graph.ExecuteAsync(Job(settings, """{"note":"{{secrets.SECRET}}"}"""), CancellationToken.None);

        sender.Calls[0].Body!.ShouldContain("{{secrets.SECRET}}");
        sender.Calls[0].Body!.ShouldNotContain("shh");
    }

    [Fact]
    public async Task FR_CON_016_the_slack_connector_posts_a_message_and_reports_slack_errors()
    {
        var sender = new Scripted(call => call.Body!.Contains("#nope", StringComparison.Ordinal) ? Json("""{"ok":false,"error":"channel_not_found"}""") : Json("""{"ok":true,"ts":"1.2","channel":"C1"}"""));
        var (http, _) = Http(sender);
        var slack = new SlackConnectorHandler(http, new Secrets());
        var settings = new Dictionary<string, string> { ["token"] = "{{secrets.SLACK}}", ["channel"] = "#ops", ["text"] = "Order {{id}} failed" };

        var ok = await slack.ExecuteAsync(Job(settings, """{"id":7}"""), CancellationToken.None);
        var bad = await slack.ExecuteAsync(Job(new(settings) { ["channel"] = "#nope" }, """{"id":7}"""), CancellationToken.None);
        var missing = await slack.ExecuteAsync(Job(new() { ["text"] = "x" }), CancellationToken.None);

        ok.Variables!["slack"]!["ts"]!.GetValue<string>().ShouldBe("1.2");
        sender.Calls[0].Url.ToString().ShouldBe("https://slack.com/api/chat.postMessage");
        sender.Calls[0].Headers["Authorization"].ShouldBe("Bearer xoxb-1");
        JsonNode.Parse(sender.Calls[0].Body!)!["text"]!.GetValue<string>().ShouldBe("Order 7 failed");
        bad.Message.ShouldBe("Slack refused the message: channel_not_found");
        missing.Message!.ShouldContain("channel");
    }
}
