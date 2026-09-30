using SmartAgri.Application.Validation;
using SmartAgri.Domain;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>A test case's fields, validated and normalized, ready to construct or apply to an
/// <see cref="AssistantTestCase"/>.</summary>
public sealed record AssistantTestCaseDetails(
    string Question,
    AssistantTestCaseCategory Category,
    AssistantTestExpectedKind ExpectedKind,
    IReadOnlyList<Guid> ExpectedDocumentIds,
    Guid? FollowUpOfId);

/// <summary>
/// Validation of <c>POST</c>／<c>PATCH /api/v1/assistants/{id}/test-cases[/{caseId}]</c> and the
/// bulk import endpoint (M3.5 plan §4/§5 Slice 1, issue #123): at most <see cref="MaxCount"/>
/// test cases per assistant, a question of 1–<see cref="AssistantTestCase.QuestionMaxLength"/>
/// characters, a declared <see cref="AssistantTestCaseCategory"/> and
/// <see cref="AssistantTestExpectedKind"/>, documents that belong to the assistant's currently
/// connected knowledge bases, and a follow-up target that is another test case of the same
/// assistant.
/// </summary>
public static class AssistantTestCaseRules
{
    /// <summary>The most test cases one assistant may have (issue #123 acceptance: the 51st is 422).</summary>
    public const int MaxCount = 50;

    public const string QuestionField = "question";

    public const string CategoryField = "category";

    public const string ExpectedKindField = "expectedKind";

    public const string ExpectedDocumentIdsField = "expectedDocumentIds";

    public const string FollowUpOfField = "followUpOfId";

    public const string QuestionRequiredMessage = "請輸入測試題目的問題。";

    public static readonly string QuestionTooLongMessage = $"問題最多 {AssistantTestCase.QuestionMaxLength} 個字。";

    public const string CategoryInvalidMessage = "題目分類不正確。";

    public const string ExpectedKindInvalidMessage = "預期回覆類型不正確。";

    public const string ExpectedDocumentsNotAllowedMessage = "預期回覆類型不是「company-data」時，不能指定應被引用的文件。";

    public const string ExpectedDocumentNotConnectableMessage = "應被引用的文件必須屬於這個助理目前連接的知識庫。";

    public const string FollowUpMustBeSameAssistantMessage = "追問的題目必須是同一個助理底下、已存在的題目。";

    public const string TooManyTestCasesMessage = "每個助理最多可以建立 50 題測試題。";

    /// <summary>For creating a new test case: refuses outright at <see cref="MaxCount"/>, before
    /// validating anything else.</summary>
    public static ValidationResult<AssistantTestCaseDetails> ForCreate(
        int existingCount,
        string? question,
        string? category,
        string? expectedKind,
        IReadOnlyList<Guid>? expectedDocumentIds,
        Guid? followUpOfId,
        IReadOnlyCollection<Guid> connectedDocumentIds,
        IReadOnlyCollection<Guid> existingCaseIds)
    {
        if (existingCount >= MaxCount)
        {
            return ValidationResult<AssistantTestCaseDetails>.Invalid(QuestionField, TooManyTestCasesMessage);
        }

        return Validate(question, category, expectedKind, expectedDocumentIds, followUpOfId, connectedDocumentIds, existingCaseIds);
    }

    /// <summary>For a partial update: a <see langword="null"/> field keeps its
    /// <paramref name="current"/> value.</summary>
    public static ValidationResult<AssistantTestCaseDetails> ForUpdate(
        AssistantTestCaseDetails current,
        string? question,
        string? category,
        string? expectedKind,
        IReadOnlyList<Guid>? expectedDocumentIds,
        Guid? followUpOfId,
        IReadOnlyCollection<Guid> connectedDocumentIds,
        IReadOnlyCollection<Guid> existingCaseIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        return Validate(
            question ?? current.Question,
            category ?? WireNames<AssistantTestCaseCategory>.ToWire(current.Category),
            expectedKind ?? WireNames<AssistantTestExpectedKind>.ToWire(current.ExpectedKind),
            expectedDocumentIds ?? current.ExpectedDocumentIds,
            followUpOfId ?? current.FollowUpOfId,
            connectedDocumentIds,
            existingCaseIds);
    }

    /// <summary>The shared core rule, also used directly by bulk import (each row validated the
    /// same way one at a time).</summary>
    public static ValidationResult<AssistantTestCaseDetails> Validate(
        string? question,
        string? category,
        string? expectedKind,
        IReadOnlyList<Guid>? expectedDocumentIds,
        Guid? followUpOfId,
        IReadOnlyCollection<Guid> connectedDocumentIds,
        IReadOnlyCollection<Guid> existingCaseIds)
    {
        ArgumentNullException.ThrowIfNull(connectedDocumentIds);
        ArgumentNullException.ThrowIfNull(existingCaseIds);

        var failures = new List<ValidationFailure>();

        var trimmedQuestion = (question ?? string.Empty).Trim();
        if (trimmedQuestion.Length == 0)
        {
            failures.Add(new ValidationFailure(QuestionField, QuestionRequiredMessage));
        }
        else if (trimmedQuestion.Length > AssistantTestCase.QuestionMaxLength)
        {
            failures.Add(new ValidationFailure(QuestionField, QuestionTooLongMessage));
        }

        var resolvedCategory = AssistantTestCaseCategory.Common;
        if (category is null || !TryParse(category, out resolvedCategory))
        {
            failures.Add(new ValidationFailure(CategoryField, CategoryInvalidMessage));
        }

        var resolvedKind = AssistantTestExpectedKind.CompanyData;
        if (expectedKind is null || !TryParse(expectedKind, out resolvedKind))
        {
            failures.Add(new ValidationFailure(ExpectedKindField, ExpectedKindInvalidMessage));
        }

        var documentIds = (expectedDocumentIds ?? []).Distinct().ToList();
        if (resolvedKind != AssistantTestExpectedKind.CompanyData && documentIds.Count > 0)
        {
            failures.Add(new ValidationFailure(ExpectedDocumentIdsField, ExpectedDocumentsNotAllowedMessage));
        }
        else
        {
            foreach (var documentId in documentIds)
            {
                if (!connectedDocumentIds.Contains(documentId))
                {
                    failures.Add(new ValidationFailure(ExpectedDocumentIdsField, ExpectedDocumentNotConnectableMessage));
                    break;
                }
            }
        }

        if (followUpOfId is { } followUp && !existingCaseIds.Contains(followUp))
        {
            failures.Add(new ValidationFailure(FollowUpOfField, FollowUpMustBeSameAssistantMessage));
        }

        if (failures.Count > 0)
        {
            return ValidationResult<AssistantTestCaseDetails>.Invalid(failures);
        }

        return ValidationResult<AssistantTestCaseDetails>.Valid(
            new AssistantTestCaseDetails(trimmedQuestion, resolvedCategory, resolvedKind, documentIds, followUpOfId));
    }

    private static bool TryParse<TEnum>(string wire, out TEnum value)
        where TEnum : struct, Enum
    {
        if (WireNames<TEnum>.All.Contains(wire))
        {
            value = WireNames<TEnum>.Parse(wire);
            return true;
        }

        value = default;
        return false;
    }
}
