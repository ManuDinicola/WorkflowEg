using System.Text.Json.Nodes;
using Flowd.Application.Connectors;
using Flowd.Application.Jobs;
using Microsoft.Extensions.Time.Testing;

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

    private static HttpConnectorHandler Handler(FakeSender sender, params string[] hosts) => Handler(sender, new FakeTimeProvider(), hosts);

    private static HttpConnectorHandler Handler(FakeSender sender, TimeProvider clock, params string[] hosts)
    {
        var options = new ConnectorOptions { AllowedHosts = hosts };
        var secrets = new Secrets(("TOKEN", "s3cret"), ("CLIENT", "id-1"), ("SECRET", "shh"));
        return new HttpConnectorHandler(sender, secrets, options, new HttpAuthenticator(sender, secrets, options, clock));
    }

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

    private sealed class FakeMail : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static EmailConnectorHandler Mail(FakeMail mail, string host = "smtp.example.com", params string[] domains) =>
        new(mail, new Secrets(), new ConnectorOptions { Smtp = new SmtpSettings { Host = host, From = "flowd@example.com", AllowedRecipientDomains = domains } });

    [Fact]
    public async Task FR_CON_012_the_email_connector_renders_templates_and_reports_the_recipient_count()
    {
        var mail = new FakeMail();
        var job = Job(new() { ["to"] = "{{who}}@example.com; b@example.com", ["subject"] = "Order {{id}}", ["body"] = "Hello {{who}}" }, """{"who":"ann","id":5}""");

        var result = await Mail(mail, domains: "example.com").ExecuteAsync(job, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Variables!["email"]!["sent"]!.GetValue<int>().ShouldBe(2);
        var sent = mail.Sent.Single();
        sent.To.ShouldBe(["ann@example.com", "b@example.com"]);
        (sent.From, sent.Subject, sent.Body).ShouldBe(("flowd@example.com", "Order 5", "Hello ann"));
    }

    [Fact]
    public async Task FR_CON_012_unlisted_domains_missing_smtp_and_header_injection_are_refused_without_sending()
    {
        var mail = new FakeMail();
        var ok = new Dictionary<string, string> { ["to"] = "a@evil.com", ["subject"] = "x" };

        (await Mail(mail, domains: "example.com").ExecuteAsync(Job(ok), CancellationToken.None)).Message!.ShouldContain("AllowedRecipientDomains");
        (await Mail(mail, host: string.Empty, "example.com").ExecuteAsync(Job(new() { ["to"] = "a@example.com" }), CancellationToken.None)).Message!.ShouldContain("Smtp:Host");
        (await Mail(mail, domains: "example.com").ExecuteAsync(Job(new() { ["to"] = "a@example.com", ["subject"] = "hi\nBcc: x@y.z" }), CancellationToken.None)).Success.ShouldBeFalse();
        mail.Sent.ShouldBeEmpty();
    }

    private sealed class FakeQueue : IQueuePublisher
    {
        public List<QueueMessage> Sent { get; } = [];

        public Task PublishAsync(QueueMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static QueueConnectorHandler Queue(FakeQueue queue, string address = "amqp://localhost", params string[] exchanges) =>
        new(queue, new Secrets(), new ConnectorOptions { Queue = new QueueSettings { Address = address, AllowedExchanges = exchanges } });

    [Fact]
    public async Task FR_CON_013_the_queue_connector_renders_templates_and_publishes_to_an_allowed_exchange()
    {
        var queue = new FakeQueue();
        var job = Job(new() { ["exchange"] = "orders", ["routingKey"] = "new.{{region}}", ["body"] = """{"id":{{id}}}""" }, """{"id":3,"region":"eu"}""");

        var result = await Queue(queue, exchanges: "orders").ExecuteAsync(job, CancellationToken.None);

        result.Success.ShouldBeTrue();
        queue.Sent.Single().ShouldBe(new QueueMessage("orders", "new.eu", "application/json", """{"id":3}"""));
    }

    [Fact]
    public async Task FR_CON_013_other_exchanges_missing_broker_and_a_default_exchange_without_queue_are_refused()
    {
        var queue = new FakeQueue();

        (await Queue(queue, exchanges: "orders").ExecuteAsync(Job(new() { ["exchange"] = "secret", ["routingKey"] = "x" }), CancellationToken.None)).Message!.ShouldContain("AllowedExchanges");
        (await Queue(queue, address: string.Empty, exchanges: "*").ExecuteAsync(Job(new() { ["exchange"] = "orders", ["routingKey"] = "x" }), CancellationToken.None)).Message!.ShouldContain("Queue:Address");
        (await Queue(queue, exchanges: "(default)").ExecuteAsync(Job(new() { ["exchange"] = string.Empty }), CancellationToken.None)).Message!.ShouldContain("routingKey");
        queue.Sent.ShouldBeEmpty();
    }
}
