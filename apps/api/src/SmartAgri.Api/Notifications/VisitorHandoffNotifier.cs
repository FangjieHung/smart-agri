using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Notifications;
using SmartAgri.Application.Observability;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Notifications;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Notifications;

/// <summary>
/// Records a 轉真人 (#355) when a website or LINE visitor was given the assistant's
/// 「找不到資料時的回覆」: a <see cref="VisitorHandoff"/> for the assistant's owner and — only when the
/// deployment has SMTP and the owner gave an address and did not opt out — a
/// <see cref="SendHandoffEmailJob"/> in the same save. Called by the visitor channels after the reply,
/// in their scope (acting for the assistant's organization).
/// </summary>
/// <remarks>
/// <b>Never affects the conversation.</b> Nothing here throws: a failure is logged (types only) and
/// dropped, and the Email itself is sent later by the job queue, with retries. Nothing of the visitor
/// is written — the conversation key only feeds <see cref="VisitorHandoffThrottle"/>'s in-memory hash.
/// </remarks>
public sealed class VisitorHandoffNotifier
{
    private readonly AppDbContext _dbContext;
    private readonly IEmailSender _email;
    private readonly VisitorHandoffThrottle _throttle;
    private readonly TimeProvider _clock;
    private readonly ILogger<VisitorHandoffNotifier> _logger;

    public VisitorHandoffNotifier(
        AppDbContext dbContext,
        IEmailSender email,
        VisitorHandoffThrottle throttle,
        TimeProvider clock,
        ILogger<VisitorHandoffNotifier> logger)
    {
        _dbContext = dbContext;
        _email = email;
        _throttle = throttle;
        _clock = clock;
        _logger = logger;
    }

    /// <param name="conversation">What identifies the visitor's conversation: the website visitor id or
    /// the LINE chat id. Hashed in memory only, never stored or logged.</param>
    public async Task NotifyAsync(Assistant assistant, VisitorHandoffChannel channel, string conversation)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        if (!_throttle.TryEnter(assistant.Id, channel, conversation))
        {
            return;
        }

        VisitorHandoff? handoff = null;
        BackgroundJob? job = null;
        try
        {
            var now = _clock.GetUtcNow();
            handoff = VisitorHandoff.Open(assistant.OrganizationId, assistant.Id, channel, now);
            _dbContext.VisitorHandoffs.Add(handoff);
            if (_email.IsConfigured && await OwnerWantsEmailAsync(assistant.OwnerAccountId))
            {
                job = BackgroundJob.Create(assistant.OrganizationId, SendHandoffEmailJob.Kind, new SendHandoffEmailJob(handoff.Id), now);
                _dbContext.BackgroundJobs.Add(job);
            }

            // Not the request's token: the reply has been given, and the record should not be lost
            // because the visitor closed the window right after it.
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            _logger.LogInformation(
                "Visitor handoff {HandoffId} recorded for assistant {AssistantId} ({Channel}); Email queued: {EmailQueued}.",
                handoff.Id, assistant.Id, WireName(channel), job is not null);
        }
        catch (Exception exception)
        {
            _throttle.Release(assistant.Id, channel, conversation);
            Detach(handoff);
            Detach(job);
            _logger.LogError(
                "A visitor handoff for assistant {AssistantId} ({Channel}) could not be recorded: {Failure}",
                assistant.Id, WireName(channel), ExceptionSummary.Of(exception));
        }
    }

    private async Task<bool> OwnerWantsEmailAsync(Guid ownerAccountId) =>
        await _dbContext.AccountNotificationSettings.AsNoTracking()
            .AnyAsync(settings => settings.AccountId == ownerAccountId && settings.HandoffEmails && settings.Email != null);

    /// <summary>The scope may save again (LINE handles a whole delivery in one): a row that failed must
    /// not be retried by someone else's save.</summary>
    private void Detach(object? entity)
    {
        if (entity is not null)
        {
            _dbContext.Entry(entity).State = EntityState.Detached;
        }
    }

    private static string WireName(VisitorHandoffChannel channel) => Domain.WireNames<VisitorHandoffChannel>.ToWire(channel);
}
