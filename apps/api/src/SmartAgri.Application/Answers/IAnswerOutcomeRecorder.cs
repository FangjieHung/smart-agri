using SmartAgri.Domain.Answers;

namespace SmartAgri.Application.Answers;

/// <summary>
/// Saves <see cref="AnswerOutcome"/> rows (M3.5 plan §3, §4, Slice 6): an interface so
/// <see cref="GroundedAnswerService"/>'s rules are unit tested without a database, mirroring
/// <c>SmartAgri.Infrastructure.Ai.IModelInvocationRecorder</c>.
/// </summary>
public interface IAnswerOutcomeRecorder
{
    /// <summary>
    /// Saves one outcome now, in a context of its own (an operational-tracking row must not
    /// depend on, or roll back with, the caller's own transaction). Never throws for the
    /// caller: implementations only ever fail loudly through logging — a write failure here must
    /// not fail the conversation or trial answer it is about (M3.5 issue #128).
    /// <paramref name="usedCandidates"/>: <see cref="AnswerOutcome.UsedCandidates"/> (#302).
    /// </summary>
    Task RecordAsync(
        Guid organizationId,
        Guid? assistantId,
        AnswerOutcomeChannel channel,
        AnswerReplyKind replyKind,
        AnswerRejectionReason? rejectionReason,
        IReadOnlyCollection<Guid> citedDocumentIds,
        bool usedCandidates,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// Saves one <see cref="AnswerReplyKind.DatabaseQuery"/> outcome (M4 #178,
    /// <see cref="AnswerOutcome.RecordDatabaseQuery"/>): the result category only. Same rules as
    /// <see cref="RecordAsync"/> — its own context, never throws for the caller.
    /// </summary>
    Task RecordDatabaseQueryAsync(
        Guid organizationId,
        Guid assistantId,
        AnswerDatabaseQueryResult result,
        DateTimeOffset at,
        CancellationToken cancellationToken);
}
