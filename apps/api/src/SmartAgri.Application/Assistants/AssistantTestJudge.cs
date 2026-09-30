using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>Whether one test case passed, and if not, why.</summary>
/// <param name="FailureReason"><see langword="null"/> exactly when <paramref name="Passed"/>.</param>
public sealed record AssistantTestVerdict(bool Passed, AssistantTestFailureReason? FailureReason)
{
    public static AssistantTestVerdict Pass { get; } = new(true, null);

    public static AssistantTestVerdict Fail(AssistantTestFailureReason reason) => new(false, reason);
}

/// <summary>
/// Judges a test-set answer (M3.5 plan §3 「通過與否只看結構化條件」, issue #124) from structure
/// only — the reply kind and the documents it cites — never from the model's text, which varies
/// from run to run:
/// <list type="bullet">
/// <item>the actual reply kind must equal <see cref="AssistantTestCase.ExpectedKind"/>, otherwise
/// <see cref="AssistantTestFailureReason.KindMismatch"/> (e.g. expected <c>no-result</c> but it
/// answered, or expected <c>company-data</c> but it found nothing);</item>
/// <item>when <c>company-data</c> is expected (and answered), every one of
/// <see cref="AssistantTestCase.ExpectedDocumentIds"/> must be among the cited documents,
/// otherwise <see cref="AssistantTestFailureReason.MissingDocument"/>. Citing additional
/// documents is fine; no expected documents means any citation passes.</item>
/// </list>
/// </summary>
public static class AssistantTestJudge
{
    public static AssistantTestVerdict Judge(
        AssistantTestExpectedKind expectedKind,
        IReadOnlyCollection<Guid> expectedDocumentIds,
        AnswerReplyKind actualKind,
        IReadOnlyCollection<Guid> citedDocumentIds)
    {
        ArgumentNullException.ThrowIfNull(expectedDocumentIds);
        ArgumentNullException.ThrowIfNull(citedDocumentIds);

        if (actualKind != ToReplyKind(expectedKind))
        {
            return AssistantTestVerdict.Fail(AssistantTestFailureReason.KindMismatch);
        }

        if (expectedKind == AssistantTestExpectedKind.CompanyData
            && !expectedDocumentIds.All(citedDocumentIds.Contains))
        {
            return AssistantTestVerdict.Fail(AssistantTestFailureReason.MissingDocument);
        }

        return AssistantTestVerdict.Pass;
    }

    private static AnswerReplyKind ToReplyKind(AssistantTestExpectedKind kind) => kind switch
    {
        AssistantTestExpectedKind.CompanyData => AnswerReplyKind.CompanyData,
        AssistantTestExpectedKind.GeneralKnowledge => AnswerReplyKind.GeneralKnowledge,
        AssistantTestExpectedKind.NoResult => AnswerReplyKind.NoResult,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown expected kind."),
    };
}
