using System.Text.Json.Nodes;
using Flowd.Application.Connectors;
using Flowd.Application.Jobs;

namespace Flowd.Application.Tests;

/// <summary>Templates and the HTTP connector rules (FR-CON-010).</summary>
public sealed class ConnectorTests
{
    private sealed class Secrets(params (string Name, string Value)[] values) : ISecretProvider
    {
        public string? Find(string name) => values.FirstOrDefault(v => v.Name == name).Value;
    }

    private sealed class FakeSender(HttpReply reply) : IHttpCallSender
    {
        public List<HttpCall> Calls { get; } = [];

        public Task<HttpReply> SendAsync(HttpCall request, TimeSpan timeout, int maxResponseBytes, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return Task.FromResult(reply);
        }
    }

    private static ActivatedJob Job(Dictionary<string, string> headers, string variables = "{}") =>
        new(1, "io.flowd:http", 2, "A", 3, DateTimeOffset.UnixEpoch, JsonNode.Parse(variables)!.AsObject(), headers, DateTimeOffset.UnixEpoch);

    private static HttpConnectorHandler Handler(FakeSender sender, params string[] hosts) =>
        new(sender, new Secrets(("TOKEN", "s3cret")), new ConnectorOptions { AllowedHosts = hosts });

    [Fact]
    public void FR_CON_010_templates_fill_variables_paths_and_secrets_and_refuse_unknown_names()
    {
        var variables = JsonNode.Parse("""{"order":{"id":42,"name":"x"},"flag":true}""")!.AsObject();
        var secrets = new Secrets(("KEY", "abc"));

        Template.Render("id={{ order.id }} name={{order.name}} flag={{flag}} key={{secrets.KEY}}", variables, secrets).ShouldBe("id=42 name=x flag=true key=abc");
        Should.Throw<ConnectorException>(() => Template.Render("{{missing}}", variables, secrets)).Message.ShouldContain("missing");
        Should.Throw<ConnectorException>(() => Template.Render("{{secrets.NOPE}}", variables, secrets)).Message.ShouldContain("NOPE");
    }

    [Fact]
    public async Task FR_CON_010_a_successful_call_returns_status_and_parsed_json_in_the_result_variable()
    {
        var sender = new FakeSender(new HttpReply(200, "application/json", """{"ok":true}"""));
        var handler = Handler(sender, "api.example.com");
        var job = Job(new() { ["url"] = "https://api.example.com/orders/{{id}}", ["method"] = "post", ["body"] = """{"id":{{id}}}""", ["headers"] = """{"Authorization":"Bearer {{secrets.TOKEN}}"}""", ["resultVariable"] = "result" }, """{"id":7}""");

        var result = await handler.ExecuteAsync(job, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Variables!["result"]!["status"]!.GetValue<int>().ShouldBe(200);
        result.Variables["result"]!["body"]!["ok"]!.GetValue<bool>().ShouldBeTrue();
        var call = sender.Calls.Single();
        call.Method.ShouldBe("POST");
        call.Url.ToString().ShouldBe("https://api.example.com/orders/7");
        call.Body.ShouldBe("""{"id":7}""");
        call.Headers["Authorization"].ShouldBe("Bearer s3cret");
    }

    [Fact]
    public async Task FR_CON_010_hosts_outside_the_allow_list_and_bad_urls_are_refused_before_any_call()
    {
        var sender = new FakeSender(new HttpReply(200, null, ""));

        (await Handler(sender).ExecuteAsync(Job(new() { ["url"] = "https://a.example.com" }), CancellationToken.None)).Message!.ShouldContain("AllowedHosts");
        (await Handler(sender, "b.example.com").ExecuteAsync(Job(new() { ["url"] = "https://a.example.com" }), CancellationToken.None)).Success.ShouldBeFalse();
        (await Handler(sender, "*").ExecuteAsync(Job(new() { ["url"] = "file:///etc/passwd" }), CancellationToken.None)).Message!.ShouldContain("http or https");
        (await Handler(sender, "*").ExecuteAsync(Job(new() { ["method"] = "GET" }), CancellationToken.None)).Message!.ShouldContain("needs a url");
        sender.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task FR_CON_010_error_statuses_fail_the_job_unless_failOnError_is_false()
    {
        var sender = new FakeSender(new HttpReply(503, "text/plain", "down"));
        var strict = await Handler(sender, "*").ExecuteAsync(Job(new() { ["url"] = "http://h.example.com" }), CancellationToken.None);
        var lenient = await Handler(sender, "*").ExecuteAsync(Job(new() { ["url"] = "http://h.example.com", ["failOnError"] = "false" }), CancellationToken.None);

        strict.Success.ShouldBeFalse();
        strict.Message!.ShouldContain("503");
        lenient.Success.ShouldBeTrue();
        lenient.Variables!["response"]!["body"]!.GetValue<string>().ShouldBe("down");
    }

    [Fact]
    public async Task FR_CON_010_missing_variables_and_secrets_fail_the_job_with_a_clear_message()
    {
        var sender = new FakeSender(new HttpReply(200, null, ""));

        var result = await Handler(sender, "*").ExecuteAsync(Job(new() { ["url"] = "http://h.example.com/{{nope}}" }), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("nope");
        sender.Calls.ShouldBeEmpty();
    }
}
