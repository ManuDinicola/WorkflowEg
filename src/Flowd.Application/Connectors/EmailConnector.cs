using System.Text.Json.Nodes;
using Flowd.Application.Jobs;

namespace Flowd.Application.Connectors;

/// <summary>An e-mail the connector wants to send.</summary>
public sealed record EmailMessage(string From, IReadOnlyList<string> To, string Subject, string Body, bool IsHtml);

/// <summary>Port: sends e-mail through the configured SMTP server.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>SMTP settings, bound from <c>Flowd:Connectors:Smtp</c>. Without a <see cref="Host"/> the e-mail connector fails its jobs.</summary>
public sealed class SmtpSettings
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 587;

    public bool UseSsl { get; init; } = true;

    public string User { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>Default sender when the task has no <c>from</c>.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Recipient domains that may be mailed (exact, case-insensitive). Empty refuses every mail; <c>*</c> allows any.</summary>
    public string[] AllowedRecipientDomains { get; init; } = [];
}

/// <summary>
/// The built-in e-mail connector (FR-CON-012): <c>&lt;flowd:connector type="email" to="a@x.com,b@x.com" subject="..." body="..." html="false"
/// from="..." resultVariable="mail"/&gt;</c>. All text settings are templates (see <see cref="Template"/>). The result variable gets <c>{ sent: n }</c>.
/// </summary>
public sealed class EmailConnectorHandler(IEmailSender sender, ISecretProvider secrets, ConnectorOptions options) : IConnectorHandler
{
    public string TaskType => Flowd.Bpmn.Model.ServiceTask.EmailConnectorTaskType;

    public async Task<ConnectorResult> ExecuteAsync(ActivatedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            var message = Build(job);
            await sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var resultVariable = job.CustomHeaders.GetValueOrDefault("resultVariable") ?? "email";
            return ConnectorResult.Ok(new JsonObject { [resultVariable] = new JsonObject { ["sent"] = message.To.Count } });
        }
        catch (ConnectorException ex)
        {
            return ConnectorResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Mail.SmtpException)
        {
            return ConnectorResult.Failed($"Sending the e-mail failed: {ex.GetType().Name}.");
        }
    }

    private EmailMessage Build(ActivatedJob job)
    {
        var settings = job.CustomHeaders;
        if (string.IsNullOrWhiteSpace(options.Smtp.Host))
        {
            throw new ConnectorException("Flowd:Connectors:Smtp:Host is not configured.");
        }

        var recipients = Render(settings.GetValueOrDefault("to") ?? throw new ConnectorException("The e-mail connector needs a 'to'."), job)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var recipient in recipients)
        {
            CheckRecipient(recipient);
        }

        var from = settings.TryGetValue("from", out var fromTemplate) ? Render(fromTemplate, job) : options.Smtp.From;
        var subject = Render(settings.GetValueOrDefault("subject") ?? string.Empty, job);
        if (recipients.Length == 0 || string.IsNullOrWhiteSpace(from) || HasLineBreak(from) || HasLineBreak(subject))
        {
            throw new ConnectorException("The e-mail needs a recipient and a sender, and no line breaks in sender or subject.");
        }

        var body = Render(settings.GetValueOrDefault("body") ?? string.Empty, job);
        var html = string.Equals(settings.GetValueOrDefault("html"), "true", StringComparison.OrdinalIgnoreCase);
        return new EmailMessage(from, recipients, subject, body, html);
    }

    private void CheckRecipient(string address)
    {
        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1 || HasLineBreak(address) || address.Contains(' ', StringComparison.Ordinal))
        {
            throw new ConnectorException("A recipient is not a valid e-mail address.");
        }

        var domain = address[(at + 1)..];
        if (!options.Smtp.AllowedRecipientDomains.Any(d => d == "*" || string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConnectorException($"Domain '{domain}' is not in Flowd:Connectors:Smtp:AllowedRecipientDomains.");
        }
    }

    private static bool HasLineBreak(string text) => text.Contains('\r', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal);

    private string Render(string template, ActivatedJob job) => Template.Render(template, job.Variables, secrets);
}
