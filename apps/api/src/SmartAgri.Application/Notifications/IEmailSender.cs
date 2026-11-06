namespace SmartAgri.Application.Notifications;

/// <summary>One plain-text Email to one recipient.</summary>
/// <param name="FromName">The sender's display name for this message (a shop's name for its reservation Emails, #391);
/// <see langword="null"/> or blank: the deployment's <c>Smtp:FromName</c>. The address is always the deployment's
/// <c>Smtp:From</c>. Line breaks and control characters never reach the header.</param>
public sealed record EmailMessage(string To, string Subject, string Body, string? FromName = null);

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
