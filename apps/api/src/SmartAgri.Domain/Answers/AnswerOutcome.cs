using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Answers;

/// <summary>
/// One answer's operational result (table <c>AnswerOutcomes</c>, M3.5 plan §3 「營運追蹤只存
/// 「結果」，不存內容」 and §4): written whenever a real conversation turn, a trial question, or
/// (Slice 2, #124) a test-set question is confirmed — never on a mid-stream failure or a
/// cancelled request.
/// </summary>
/// <remarks>
/// <para>
/// It never holds content: no question, answer, account or conversation-thread id — not even a
/// hash of any of it. <see cref="CitedDocumentIds"/> holds document ids only (M3.5 plan §3: "文件
/// id 不是對話內容"). A model test asserts the type has no <see cref="string"/> property at all,
/// so a future edit cannot quietly add one.
/// </para>
/// <para>
/// Retention (M3.5 plan §7 decision D; M6 plan decision B): follows the organization's
/// conversation retention (<c>Organization.RetentionDays</c>, forever by default). The daily
/// cleanup (<c>SmartAgri.Api.Organizations.RetentionCleanupService</c>, M6-4 #241) deletes every
/// outcome whose own <see cref="At"/> is before the same cutoff as the threads, whatever its
/// <see cref="Channel"/> — the website channel included. It is never deleted "with" a thread: it
/// records none. Purging an assistant's conversations (M6-5) leaves it alone.
/// </para>
/// </remarks>
public sealed class AnswerOutcome : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private AnswerOutcome()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary><see langword="null"/> for a wizard draft's trial answer, before any assistant
    /// exists.</summary>
    public Guid? AssistantId { get; private set; }

    public AnswerOutcomeChannel Channel { get; private set; }

    public AnswerReplyKind ReplyKind { get; private set; }

    /// <summary>Set only when <see cref="ReplyKind"/> is <see cref="AnswerReplyKind.NoResult"/>.</summary>
    public AnswerRejectionReason? RejectionReason { get; private set; }

    /// <summary>Set exactly when <see cref="ReplyKind"/> is <see cref="AnswerReplyKind.DatabaseQuery"/>
    /// (M4 #178): the result category only — never the query, its parameters or its numbers.</summary>
    public AnswerDatabaseQueryResult? DatabaseQueryResult { get; private set; }

    private readonly List<Guid> _citedDocumentIds = [];

    /// <summary>The documents a <c>company-data</c> reply cited; empty otherwise.</summary>
    public IReadOnlyList<Guid> CitedDocumentIds => _citedDocumentIds;

    public DateTimeOffset At { get; private set; }

    /// <summary>
    /// No passage reached the relevance threshold, and the model was asked with candidate
    /// passages above <c>Retrieval:CandidateMinScore</c> instead (pre-launch plan §3 B, #302) —
    /// so a wrong answer from a loose match can be found. Only ever set on a reply the model
    /// produced from passages: a <c>company-data</c> reply, or a <c>no-result</c> the model's
    /// answer caused (never <see cref="AnswerRejectionReason.BelowThreshold"/>, which calls no model).
    /// </summary>
    public bool UsedCandidates { get; private set; }

    /// <param name="usedCandidates"><see cref="UsedCandidates"/>.</param>
    public static AnswerOutcome Record(
        Guid organizationId,
        Guid? assistantId,
        AnswerOutcomeChannel channel,
        AnswerReplyKind replyKind,
        AnswerRejectionReason? rejectionReason,
        IReadOnlyCollection<Guid> citedDocumentIds,
        DateTimeOffset at,
        bool usedCandidates = false)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An answer outcome is always recorded for an organization.", nameof(organizationId));
        }

        if (assistantId == Guid.Empty)
        {
            throw new ArgumentException("Pass null, not Guid.Empty, for no assistant.", nameof(assistantId));
        }

        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, null);
        }

        if (!Enum.IsDefined(replyKind))
        {
            throw new ArgumentOutOfRangeException(nameof(replyKind), replyKind, null);
        }

        if (replyKind == AnswerReplyKind.DatabaseQuery)
        {
            throw new ArgumentException(
                $"A database query outcome is recorded with {nameof(RecordDatabaseQuery)}.", nameof(replyKind));
        }

        if (rejectionReason is { } reason && !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(rejectionReason), rejectionReason, null);
        }

        var isNoResult = replyKind == AnswerReplyKind.NoResult;
        if (isNoResult != rejectionReason.HasValue)
        {
            throw new ArgumentException(
                "A rejection reason is set for a no-result reply, and only for one.", nameof(rejectionReason));
        }

        ArgumentNullException.ThrowIfNull(citedDocumentIds);

        if (usedCandidates
            && (replyKind == AnswerReplyKind.GeneralKnowledge || rejectionReason is AnswerRejectionReason.BelowThreshold or AnswerRejectionReason.EmptyAnswer))
        {
            throw new ArgumentException(
                "Candidate passages are only ever used for a reply the model produced from passages.", nameof(usedCandidates));
        }

        var outcome = new AnswerOutcome
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AssistantId = assistantId,
            Channel = channel,
            ReplyKind = replyKind,
            RejectionReason = rejectionReason,
            At = at,
            UsedCandidates = usedCandidates,
        };
        outcome._citedDocumentIds.AddRange(citedDocumentIds.Distinct());
        return outcome;
    }

    /// <summary>
    /// A conversation's database query answer (M4 #149; recorded since #178): always the
    /// <see cref="AnswerOutcomeChannel.Chat"/> channel and an assistant, no rejection reason and no
    /// cited document — only <paramref name="result"/>.
    /// </summary>
    public static AnswerOutcome RecordDatabaseQuery(
        Guid organizationId, Guid assistantId, AnswerDatabaseQueryResult result, DateTimeOffset at)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An answer outcome is always recorded for an organization.", nameof(organizationId));
        }

        if (assistantId == Guid.Empty)
        {
            throw new ArgumentException("A database query is always asked of an assistant.", nameof(assistantId));
        }

        if (!Enum.IsDefined(result))
        {
            throw new ArgumentOutOfRangeException(nameof(result), result, null);
        }

        return new AnswerOutcome
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AssistantId = assistantId,
            Channel = AnswerOutcomeChannel.Chat,
            ReplyKind = AnswerReplyKind.DatabaseQuery,
            DatabaseQueryResult = result,
            At = at,
        };
    }
}
