using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Notifications;

namespace SmartAgri.Infrastructure.Notifications;

/// <summary>
/// <see cref="IEmailSender"/> over SMTP with .NET's own <see cref="SmtpClient"/> (#355): no extra
/// package for one plain-text message per notification. STARTTLS on the submission port (587); one
/// connection per message, which is plenty for notifications.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IOptions<SmtpOptions> _options;

    public SmtpEmailSender(IOptions<SmtpOptions> options)
    {
        _options = options;
    }

    public bool IsConfigured => _options.Value.IsConfigured;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var options = _options.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("No SMTP server is configured (Smtp:Host).");
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(options.From!.Trim(), options.EffectiveFromName, Encoding.UTF8),
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = message.Body,
            BodyEncoding = Encoding.UTF8,
            BodyTransferEncoding = TransferEncoding.Base64,
            IsBodyHtml = false,
        };
        mail.To.Add(new MailAddress(message.To));

        using var client = new SmtpClient(options.Host!.Trim(), options.EffectivePort)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = options.EffectiveUseStartTls,
            UseDefaultCredentials = false,
            Credentials = options.HasCredentials ? new NetworkCredential(options.Username, options.Password) : null,
            Timeout = (int)options.EffectiveTimeout.TotalMilliseconds,
        };

        // SendMailAsync ignores Timeout; the job's own cancellation still applies.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.EffectiveTimeout);
        try
        {
            await client.SendMailAsync(mail, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The SMTP server did not accept the message within {options.EffectiveTimeout.TotalSeconds:0} seconds.");
        }
    }
}
