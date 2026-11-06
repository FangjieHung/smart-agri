using System.Net.Mail;
using SmartAgri.Application.Observability;

namespace SmartAgri.Api.Notifications;

/// <summary>How an Email job describes a send that failed (#355, #391): stored as the job's <c>LastError</c> and logged.</summary>
internal static class EmailFailures
{
    /// <summary>The exception types and, for an <see cref="SmtpException"/>, the SMTP status — never the message: a
    /// server's reply can quote the recipient's address.</summary>
    public static string Summary(Exception exception) =>
        exception is SmtpException smtp ? $"{ExceptionSummary.Of(exception)} (SMTP {smtp.StatusCode})" : ExceptionSummary.Of(exception);
}
