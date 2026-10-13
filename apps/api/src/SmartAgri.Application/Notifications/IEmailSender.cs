namespace SmartAgri.Application.Notifications;

/// <summary>One plain-text Email to one recipient.</summary>
public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>
/// Sends notification Emails (#355) through the deployment's SMTP server (<c>Smtp</c> section).
/// Only background jobs call it, so a slow or failing mail server never holds up a request.
/// </summary>
public interface IEmailSender
{
    /// <summary>Whether the deployment configured an SMTP server; without one nothing is ever sent and
    /// nothing is enqueued.</summary>
    bool IsConfigured { get; }

    /// <summary>Hands <paramref name="message"/> to the SMTP server. Throws on any failure (the job is
    /// then retried); the exception message never contains the server's password.</summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
