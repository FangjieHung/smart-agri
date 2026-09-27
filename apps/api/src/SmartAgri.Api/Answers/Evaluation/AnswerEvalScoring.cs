using SmartAgri.Application.Answers;

namespace SmartAgri.Api.Answers.Evaluation;

/// <summary>One question's answer, as the evaluation judges it.</summary>
/// <param name="ReplyText">The final reply text (the refusal message for <c>no-result</c>).</param>
/// <param name="CitedDocuments">The document names the reply actually cited, in citation order;
/// empty unless the reply is <see cref="GroundedReplyKind.CompanyData"/>.</param>
public sealed record AnswerEvalQuestionResult(
    AnswerEvalQuestion Question,
    GroundedReplyKind ActualKind,
    GroundedRejectionReason? RejectionReason,
    IReadOnlyList<string> CitedDocuments,
    string ReplyText)
{
    /// <summary>Whether the reply's kind is the one the question expects (the only two kinds a
    /// <c>company-data-only</c> profile ever produces: <c>company-data</c> or <c>no-result</c>).</summary>
    public bool KindCorrect => Question.ExpectedKind switch
    {
        AnswerEvalExpectedKind.CompanyData => ActualKind == GroundedReplyKind.CompanyData,
        AnswerEvalExpectedKind.NoResult => ActualKind == GroundedReplyKind.NoResult,
        _ => false,
    };

    /// <summary>For a <c>company-data</c> question: whether the reply cited at least one of the
    /// documents it should have. <see langword="null"/> for a <c>no-result</c> question (citation
    /// hit is not defined for it).</summary>
    public bool? CitationHit => Question.ExpectedKind == AnswerEvalExpectedKind.CompanyData
        ? Question.ExpectedCitedDocuments.Any(CitedDocuments.Contains)
        : null;
}

/// <param name="ReplyKindAccuracy">Questions whose reply kind matched what was expected, over the total.</param>
/// <param name="CitationHitRate">Of the <c>company-data</c> questions, those that cited at least
/// one expected document, over the total <c>company-data</c> questions; <see langword="null"/>
/// when the bank has none.</param>
/// <param name="RejectionReasons">How many <c>no-result</c> replies (whether expected or not)
/// carried each <see cref="GroundedRejectionReason"/>, most common first (grounded-answers ADR:
/// the bank must show the rejection reason distribution so the prompt and threshold can be tuned).</param>
/// <param name="AverageInputTokens">Mean of <c>ModelInvocations.InputTokens</c> over every
/// <c>generate-answer</c> call this run made; <see langword="null"/> when none reported it.</param>
/// <param name="AverageOutputTokens">The same for <c>OutputTokens</c>.</param>
public sealed record AnswerEvalSummary(
    int Total,
    int ReplyKindCorrect,
    double ReplyKindAccuracy,
    int CompanyDataQuestions,
    int CitationHits,
    double? CitationHitRate,
    IReadOnlyList<(GroundedRejectionReason Reason, int Count)> RejectionReasons,
    double? AverageInputTokens,
    double? AverageOutputTokens);

/// <summary>
/// How the answer evaluation (M3 plan Slice 13; ticket #83) judges and sums up what
/// <see cref="GroundedAnswerService.AnswerAsync"/> returned: pure except for the token averages
/// (computed by the caller from <c>ModelInvocations</c>, since that is not part of the reply),
/// so every rule other than token usage is unit tested without a database.
/// </summary>
public static class AnswerEvalScoring
{
    public static AnswerEvalQuestionResult Judge(AnswerEvalQuestion question, GroundedReply reply)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(reply);
        return new AnswerEvalQuestionResult(
            question,
            reply.Kind,
            reply.RejectionReason,
            [.. reply.Citations.Select(citation => citation.DocumentName)],
            reply.Text);
    }

    public static AnswerEvalSummary Summarize(
        IReadOnlyList<AnswerEvalQuestionResult> results, double? averageInputTokens, double? averageOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(results);

        var companyData = results.Where(result => result.Question.ExpectedKind == AnswerEvalExpectedKind.CompanyData).ToList();
        var rejections = results
            .Where(result => result.RejectionReason is not null)
            .GroupBy(result => result.RejectionReason!.Value)
            .Select(group => (Reason: group.Key, Count: group.Count()))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Reason)
            .ToList();

        return new AnswerEvalSummary(
            results.Count,
            results.Count(result => result.KindCorrect),
            results.Count == 0 ? 0 : (double)results.Count(result => result.KindCorrect) / results.Count,
            companyData.Count,
            companyData.Count(result => result.CitationHit == true),
            companyData.Count == 0 ? null : (double)companyData.Count(result => result.CitationHit == true) / companyData.Count,
            rejections,
            averageInputTokens,
            averageOutputTokens);
    }
}
