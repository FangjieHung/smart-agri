using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartAgri.Application.Answers;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Infrastructure.Answers;

/// <summary>
/// Saves each <see cref="AnswerOutcome"/> row through a context of its own, for the given
/// organization, committed at once — mirrors <c>EfModelInvocationRecorder</c>: an operational
/// row must not depend on the caller's own transaction (M3.5 plan §3, Slice 6).
/// </summary>
public sealed class EfAnswerOutcomeRecorder : IAnswerOutcomeRecorder
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IOrganizationContext _organization;
    private readonly ILogger<EfAnswerOutcomeRecorder> _logger;

    public EfAnswerOutcomeRecorder(
        DbContextOptions<AppDbContext> options, IOrganizationContext organization, ILogger<EfAnswerOutcomeRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _organization = organization;
        _logger = logger;
    }

    /// <summary>Never throws (M3.5 issue #128: a write failure here must not fail the
    /// conversation or trial answer it is about) — logs instead.</summary>
    public async Task RecordAsync(
        Guid organizationId,
        Guid? assistantId,
        AnswerOutcomeChannel channel,
        AnswerReplyKind replyKind,
        AnswerRejectionReason? rejectionReason,
        IReadOnlyCollection<Guid> citedDocumentIds,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await SaveAsync(
            () => AnswerOutcome.Record(organizationId, assistantId, channel, replyKind, rejectionReason, citedDocumentIds, at));
    }

    /// <summary>Never throws, like <see cref="RecordAsync"/>.</summary>
    public async Task RecordDatabaseQueryAsync(
        Guid organizationId,
        Guid assistantId,
        AnswerDatabaseQueryResult result,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await SaveAsync(() => AnswerOutcome.RecordDatabaseQuery(organizationId, assistantId, result, at));
    }

    private async Task SaveAsync(Func<AnswerOutcome> create)
    {
        try
        {
            var outcome = create();
            // Not cancelled with the caller: by this point the reply is already final, so the
            // outcome should still be written even if the client just disconnected.
            await using var dbContext = new AppDbContext(_options, _organization);
            dbContext.AnswerOutcomes.Add(outcome);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not record an answer outcome.");
        }
    }
}
