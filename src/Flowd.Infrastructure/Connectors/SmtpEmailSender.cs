using System.Net;
using System.Net.Mail;
using Flowd.Application.Connectors;

namespace Flowd.Infrastructure.Connectors;

/// <summary>Sends connector e-mail through the SMTP server of <see cref="SmtpSettings"/> (FR-CON-012).</summary>
public sealed class SmtpEmailSender(ConnectorOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var smtp = options.Smtp;
        using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.UseSsl, Timeout = Math.Max(options.TimeoutSeconds, 1) * 1000 };
        if (smtp.User.Length > 0)
        {
            client.Credentials = new NetworkCredential(smtp.User, smtp.Password);
        }

        using var mail = new MailMessage { From = new MailAddress(message.From), Subject = message.Subject, Body = message.Body, IsBodyHtml = message.IsHtml };
        foreach (var to in message.To)
        {
            mail.To.Add(to);
        }

        await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
    }
}
