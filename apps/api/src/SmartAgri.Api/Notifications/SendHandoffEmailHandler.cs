using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.AdminSpa;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Databases;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Notifications;
using SmartAgri.Application.Observability;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Notifications;

/// <summary>
/// Handles <see cref="SendHandoffEmailJob.Kind"/> (#355): sends one 轉真人 Email to the assistant's
/// owner — read now, so an owner who opted out or removed their address after the visitor came gets
/// nothing — and records that it was sent. Any SMTP failure is retried by the job queue (exponential
/// backoff, <c>Jobs:RetryBaseDelay</c>), up to the job's attempts; the visitor's conversation finished
/// long before.
/// </summary>
/// <remarks>
/// Nothing to send (the assistant was deleted with its handoffs, the handoff was already handled or
/// emailed, SMTP is no longer configured, the owner opted out or has no address) completes the job
/// without an Email. A failure is rethrown as <see cref="HandoffEmailFailedException"/>, whose message
/// — stored as the job's <c>LastError</c> and logged — has the exception types and SMTP status only:
/// a server's reply can quote the recipient's address.
/// </remarks>
internal sealed class SendHandoffEmailHandler : IJobHandler
{
    private readonly AppDbContext _dbContext;
    private readonly IEmailSender _sender;
    private readonly TimeProvider _clock;
    private readonly IOptions<StatisticsOptions> _statistics;
    private readonly IOptions<SmartAgriAuthenticationOptions> _authentication;
    private readonly IOptions<AdminSpaOptions> _admin;
    private readonly IOptions<PublicChannelsOptions> _publicChannels;
    private readonly ILogger<SendHandoffEmailHandler> _logger;

    public SendHandoffEmailHandler(
        AppDbContext dbContext,
        IEmailSender sender,
        TimeProvider clock,
        IOptions<StatisticsOptions> statistics,
        IOptions<SmartAgriAuthenticationOptions> authentication,
        IOptions<AdminSpaOptions> admin,
        IOptions<PublicChannelsOptions> publicChannels,
        ILogger<SendHandoffEmailHandler> logger)
    {
        _dbContext = dbContext;
        _sender = sender;
        _clock = clock;
        _statistics = statistics;
        _authentication = authentication;
        _admin = admin;
        _publicChannels = publicChannels;
        _logger = logger;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<SendHandoffEmailJob>();
        var handoff = await _dbContext.VisitorHandoffs.SingleOrDefaultAsync(candidate => candidate.Id == payload.HandoffId, cancellationToken);
        if (handoff is null || handoff.EmailSentAt is not null || !handoff.IsPending || !_sender.IsConfigured)
        {
            _logger.LogInformation(
                "Handoff Email for {HandoffId} not sent: {Why}.",
                payload.HandoffId,
                handoff is null ? "the handoff no longer exists"
                : handoff.EmailSentAt is not null ? "it was sent already"
                : !handoff.IsPending ? "the handoff was handled already"
                : "SMTP is not configured");
            return;
        }

        var assistant = await _dbContext.Assistants.AsNoTracking()
            .Where(candidate => candidate.Id == handoff.AssistantId)
            .Select(candidate => new { candidate.Name, candidate.OwnerAccountId })
            .SingleAsync(cancellationToken);
        var to = await _dbContext.AccountNotificationSettings.AsNoTracking()
            .Where(settings => settings.AccountId == assistant.OwnerAccountId && settings.HandoffEmails)
            .Select(settings => settings.Email)
            .SingleOrDefaultAsync(cancellationToken);
        if (to is null)
        {
            _logger.LogInformation(
                "Handoff Email for {HandoffId} not sent: the assistant's owner turned these Emails off or has no address.", handoff.Id);
            return;
        }

        var organizationName = await _dbContext.Organizations.AsNoTracking()
            .Where(organization => organization.Id == job.OrganizationId)
            .Select(organization => organization.Name)
            .SingleAsync(cancellationToken);
        var adminOrigin = AdminSpaClientRegistrar
            .EffectiveOrigins(_authentication.Value, _admin.Value, _publicChannels.Value)
            .FirstOrDefault();
        var message = HandoffEmail.Compose(
            to, organizationName, assistant.Name, handoff.Channel, handoff.CreatedAt, _statistics.Value.Resolve(), adminOrigin);

        try
        {
            await _sender.SendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new HandoffEmailFailedException(
                $"The handoff Email could not be sent (attempt {job.Attempt} of {job.MaxAttempts}): {Summary(exception)}");
        }

        handoff.RecordEmailSent(_clock.GetUtcNow());
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        _logger.LogInformation("Handoff Email for {HandoffId} sent (attempt {Attempt}).", handoff.Id, job.Attempt);
    }

    public Task OnFinalFailureAsync(JobContext job, string error, CancellationToken cancellationToken)
    {
        _logger.LogError("Handoff Email job {JobId} gave up after {Attempts} attempts: {Error}", job.JobId, job.Attempt, error);
        return Task.CompletedTask;
    }

    private static string Summary(Exception exception) =>
        exception is SmtpException smtp ? $"{ExceptionSummary.Of(exception)} (SMTP {smtp.StatusCode})" : ExceptionSummary.Of(exception);
}

/// <summary>A 轉真人 Email the SMTP server did not take; retried by the job queue.</summary>
public sealed class HandoffEmailFailedException(string message) : Exception(message);
