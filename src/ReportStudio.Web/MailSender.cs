using System.Net;
using System.Net.Mail;

namespace ReportStudio.Web;

/// <summary>One mail with one attachment. Every recipient has already passed <c>EmailAddress.IsValid</c> and the allowed-domain check.</summary>
public sealed record MailRequest(string From, IReadOnlyList<string> Recipients, string Subject, string Body, string AttachmentName, string AttachmentType, byte[] Attachment);

public interface IMailSender
{
    Task SendAsync(MailRequest mail, CancellationToken ct);
}

/// <summary>Sends through the SMTP server of <see cref="ServerOptions.Smtp"/>. The tests use a fake instead, so no test sends mail.</summary>
public sealed class SmtpMailSender : IMailSender
{
    private readonly Microsoft.Extensions.Options.IOptions<ServerOptions> _options;

    public SmtpMailSender(Microsoft.Extensions.Options.IOptions<ServerOptions> options) => _options = options;

    public async Task SendAsync(MailRequest mail, CancellationToken ct)
    {
        var smtp = _options.Value.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.From))
            throw new InvalidOperationException("The mail server is not set up (ReportStudio:Server:Smtp:Host and From).");

        using var message = new MailMessage { From = new MailAddress(smtp.From.Trim()), Subject = mail.Subject, Body = mail.Body, IsBodyHtml = false };
        foreach (var recipient in mail.Recipients) message.To.Add(new MailAddress(recipient));

        using var data = new MemoryStream(mail.Attachment, writable: false);
        message.Attachments.Add(new Attachment(data, mail.AttachmentName, mail.AttachmentType));

        using var client = new SmtpClient(smtp.Host.Trim(), smtp.Port) { EnableSsl = smtp.UseSsl, Timeout = 60_000 };
        if (!string.IsNullOrEmpty(smtp.User)) client.Credentials = new NetworkCredential(smtp.User, smtp.Password);
        await client.SendMailAsync(message, ct).ConfigureAwait(false);
    }
}
