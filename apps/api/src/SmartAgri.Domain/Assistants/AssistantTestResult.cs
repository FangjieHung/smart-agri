using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// One test case's outcome within an <see cref="AssistantTestRun"/> (M3.5 plan §4). A snapshot:
/// the question, what it expected and what it actually got are copied in, so the history still
/// reads correctly after the test case is edited or deleted (<see cref="TestCaseId"/> is
/// deliberately not a foreign key). The test set is the owner's own test data, not anyone's
/// private conversation, so the answer text is kept (plan §3).
/// </summary>
public sealed class AssistantTestResult : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private AssistantTestResult()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid RunId { get; private set; }

    public Guid TestCaseId { get; private set; }

    /// <summary>The test case's <see cref="AssistantTestCase.Ordinal"/> at the time.</summary>
    public int Ordinal { get; private set; }

    public string QuestionSnapshot { get; private set; } = string.Empty;

    public AssistantTestExpectedKind ExpectedKind { get; private set; }

    public IReadOnlyList<Guid> ExpectedDocumentIds { get; private set; } = [];

    public AnswerReplyKind ActualKind { get; private set; }

    /// <summary>The final reply's text (for <c>no-result</c>, the assistant's refusal message).</summary>
    public string AnswerText { get; private set; } = string.Empty;

    /// <summary>The documents the reply cited, in order of first citation, without duplicates.</summary>
    public IReadOnlyList<Guid> CitedDocumentIds { get; private set; } = [];

    /// <summary>Why a <c>no-result</c> reply is one; <see langword="null"/> otherwise.</summary>
    public AnswerRejectionReason? RejectionReason { get; private set; }

    /// <summary>The best retrieval score for the question (<see langword="null"/> when nothing
    /// was found), shown next to the run's <see cref="AssistantTestRun.MinScore"/> so the owner
    /// can tell a question that is "close to the threshold" (plan §7 技術風險 2).</summary>
    public double? TopScore { get; private set; }

    public bool Passed { get; private set; }

    /// <summary><see langword="null"/> exactly when <see cref="Passed"/>.</summary>
    public AssistantTestFailureReason? FailureReason { get; private set; }

    public static AssistantTestResult Record(
        AssistantTestRun run,
        AssistantTestCase testCase,
        AnswerReplyKind actualKind,
        string answerText,
        IReadOnlyList<Guid> citedDocumentIds,
        AnswerRejectionReason? rejectionReason,
        double? topScore,
        AssistantTestFailureReason? failureReason)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(answerText);
        ArgumentNullException.ThrowIfNull(citedDocumentIds);
        if (run.AssistantId != testCase.AssistantId || run.OrganizationId != testCase.OrganizationId)
        {
            throw new ArgumentException("A test result must be for a test case of the run's own assistant.", nameof(testCase));
        }

        if (!Enum.IsDefined(actualKind))
        {
            throw new ArgumentOutOfRangeException(nameof(actualKind), actualKind, "Not a declared reply kind.");
        }

        if (failureReason is { } reason && !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(failureReason), failureReason, "Not a declared failure reason.");
        }

        return new AssistantTestResult
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = run.OrganizationId,
            RunId = run.Id,
            TestCaseId = testCase.Id,
            Ordinal = testCase.Ordinal,
            QuestionSnapshot = testCase.Question,
            ExpectedKind = testCase.ExpectedKind,
            ExpectedDocumentIds = [.. testCase.ExpectedDocumentIds],
            ActualKind = actualKind,
            AnswerText = answerText,
            CitedDocumentIds = [.. citedDocumentIds.Distinct()],
            RejectionReason = rejectionReason,
            TopScore = topScore,
            Passed = failureReason is null,
            FailureReason = failureReason,
        };
    }
}
