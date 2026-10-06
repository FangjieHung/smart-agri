using SmartAgri.Application.Validation;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Application.Cases;

/// <summary>The fields of a new case that passed <see cref="CaseRules.ValidateCreate"/>: trimmed title
/// and description, and the links as complete pairs.</summary>
public sealed record CaseCreateFields(
    Guid TypeId, Guid GroupId, DateTimeOffset DueAt, string Title, string Description, CaseLinks Links);

/// <summary>
/// The rules of <c>POST /api/v1/cases</c> that need no database (M7 plan §3 C; issue #248): every field
/// failure at once, then whether the due time is in the past (decision H, its own <c>422
/// due-in-past</c>). Whether the type is active, the group archived and each link readable now are the
/// endpoint's (they need the organization's rows and the caller's access).
/// </summary>
public static class CaseRules
{
    public const string TypeField = "typeId";

    public const string GroupField = "groupId";

    public const string DueAtField = "dueAt";

    public const string TitleField = "title";

    public const string DescriptionField = "description";

    public const string SubmissionField = "submissionId";

    public const string ThreadField = "threadId";

    public const string PreviousCaseField = "previousCaseId";

    public const string TypeRequiredMessage = "請選擇案件類型。";

    public const string GroupRequiredMessage = "請選擇承辦組。";

    public const string DueAtRequiredMessage = "請填寫處理時限。";

    public const string DueInPastMessage = "時限不能早於現在。";

    public const string TitleRequiredMessage = "請輸入案件標題。";

    public static readonly string TitleTooLongMessage = $"案件標題請在 {Case.TitleMaxLength} 個字以內。";

    public const string DescriptionTooLongMessage = "說明請在 4,000 個字以內。";

    public const string RecordPairMessage = "連結數據庫紀錄時，請同時提供數據庫與紀錄。";

    public const string ThreadPairMessage = "連結對話時，請同時提供助理與對話。";

    // --- The actions (M7-4, issue #249) --------------------------------------------------------

    public const string EventCountField = "eventCount";

    public const string NoteField = "note";

    public const string ResolutionField = "resolution";

    public const string ReasonField = "reason";

    /// <summary><c>422</c> reason: <c>:request-info</c> or a comment without its note.</summary>
    public const string NoteRequiredReason = "note-required";

    /// <summary><c>422</c> reason: <c>:complete</c> without a 處理結果.</summary>
    public const string ResolutionRequiredReason = "resolution-required";

    /// <summary><c>422</c> reason: <c>:cancel</c> without the reason it needs (<c>CaseActionRules.CancelReasonRequired</c>).</summary>
    public const string ReasonRequiredReason = "reason-required";

    public const string EventCountRequiredMessage = "缺少畫面上的案件版本（eventCount），請重新整理後再試。";

    public const string RequestInfoNoteRequiredMessage = "請說明需要補充哪些資料。";

    public const string CommentRequiredMessage = "請輸入補充內容。";

    public const string ResolutionRequiredMessage = "請填寫處理結果。";

    public const string ReasonRequiredMessage = "請填寫取消原因。";

    public const string TextTooLongMessage = "請在 2,000 個字以內。";

    /// <summary>The shape of an action's request that needs no case: <paramref name="eventCount"/> is
    /// present and each text is at most <see cref="CaseEvent.NoteMaxLength"/> once trimmed. Whether a
    /// text is <b>required</b> depends on the case and the caller, so it is checked after
    /// <c>CaseActionRules</c> (its own <c>422</c> reason).</summary>
    public static IReadOnlyList<ValidationFailure> ValidateAction(int? eventCount, params (string Field, string? Text)[] texts)
    {
        var failures = new List<ValidationFailure>();
        if (eventCount is null)
        {
            failures.Add(new ValidationFailure(EventCountField, EventCountRequiredMessage));
        }

        foreach (var (field, text) in texts)
        {
            if (Trimmed(text) is { Length: > CaseEvent.NoteMaxLength })
            {
                failures.Add(new ValidationFailure(field, TextTooLongMessage));
            }
        }

        return failures;
    }

    /// <summary>A note, resolution or reason trimmed, or <see langword="null"/> when blank.</summary>
    public static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Every field failure at once (a form shows each under its field), or the normalized fields.</summary>
    public static ValidationResult<CaseCreateFields> ValidateCreate(
        Guid? typeId,
        Guid? groupId,
        DateTimeOffset? dueAt,
        string? title,
        string? description,
        Guid? databaseId,
        Guid? submissionId,
        Guid? assistantId,
        Guid? threadId,
        Guid? previousCaseId)
    {
        var failures = new List<ValidationFailure>();
        if (typeId is not { } type || type == Guid.Empty)
        {
            failures.Add(new ValidationFailure(TypeField, TypeRequiredMessage));
        }

        if (groupId is not { } group || group == Guid.Empty)
        {
            failures.Add(new ValidationFailure(GroupField, GroupRequiredMessage));
        }

        if (dueAt is null)
        {
            failures.Add(new ValidationFailure(DueAtField, DueAtRequiredMessage));
        }

        var trimmedTitle = (title ?? string.Empty).Trim();
        if (trimmedTitle.Length == 0)
        {
            failures.Add(new ValidationFailure(TitleField, TitleRequiredMessage));
        }
        else if (trimmedTitle.Length > Case.TitleMaxLength)
        {
            failures.Add(new ValidationFailure(TitleField, TitleTooLongMessage));
        }

        var trimmedDescription = (description ?? string.Empty).Trim();
        if (trimmedDescription.Length > Case.DescriptionMaxLength)
        {
            failures.Add(new ValidationFailure(DescriptionField, DescriptionTooLongMessage));
        }

        if (!IsPair(databaseId, submissionId))
        {
            failures.Add(new ValidationFailure(SubmissionField, RecordPairMessage));
        }

        if (!IsPair(assistantId, threadId))
        {
            failures.Add(new ValidationFailure(ThreadField, ThreadPairMessage));
        }

        if (failures.Count > 0)
        {
            return ValidationResult<CaseCreateFields>.Invalid(failures);
        }

        return ValidationResult<CaseCreateFields>.Valid(new CaseCreateFields(
            typeId!.Value,
            groupId!.Value,
            dueAt!.Value,
            trimmedTitle,
            trimmedDescription,
            new CaseLinks(
                ThreadAssistantId: assistantId,
                ThreadId: threadId,
                DatabaseId: databaseId,
                SubmissionId: submissionId,
                PreviousCaseId: previousCaseId is { } previous && previous != Guid.Empty ? previous : null)));
    }

    /// <summary>Decision H: a due time may be now but never earlier.</summary>
    public static bool IsDueInPast(DateTimeOffset dueAt, DateTimeOffset now) => dueAt < now;

    private static bool IsPair(Guid? first, Guid? second) =>
        (first is null && second is null) || (first is { } a && a != Guid.Empty && second is { } b && b != Guid.Empty);
}
