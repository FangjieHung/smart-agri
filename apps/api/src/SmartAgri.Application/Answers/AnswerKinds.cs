using SmartAgri.Domain.Answers;

namespace SmartAgri.Application.Answers;

/// <summary>The pipeline's <see cref="GroundedReplyKind"/>／<see cref="GroundedRejectionReason"/>
/// as the Domain's stored equivalents (<see cref="AnswerReplyKind"/>／<see cref="AnswerRejectionReason"/>;
/// the same wire names — Domain may not reference Application). Used for <see cref="AnswerOutcome"/>
/// rows and test-run results (M3.5 Slices 2 and 6).</summary>
public static class AnswerKinds
{
    public static AnswerReplyKind ToReplyKind(GroundedReplyKind kind) => kind switch
    {
        GroundedReplyKind.CompanyData => AnswerReplyKind.CompanyData,
        GroundedReplyKind.GeneralKnowledge => AnswerReplyKind.GeneralKnowledge,
        GroundedReplyKind.NoResult => AnswerReplyKind.NoResult,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown reply kind."),
    };

    public static AnswerRejectionReason ToRejectionReason(GroundedRejectionReason reason) => reason switch
    {
        GroundedRejectionReason.BelowThreshold => AnswerRejectionReason.BelowThreshold,
        GroundedRejectionReason.CitationOutOfRange => AnswerRejectionReason.CitationOutOfRange,
        GroundedRejectionReason.NoCitation => AnswerRejectionReason.NoCitation,
        GroundedRejectionReason.CannotAnswer => AnswerRejectionReason.CannotAnswer,
        GroundedRejectionReason.EmptyAnswer => AnswerRejectionReason.EmptyAnswer,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown rejection reason."),
    };
}
